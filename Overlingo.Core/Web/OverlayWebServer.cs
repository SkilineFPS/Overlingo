using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Overlingo.Core;

/// <summary>
/// Lokální server pro OBS „Browser Source“: http://127.0.0.1:PORT/ je průhledná stránka s aktuálním překladem.
/// Poslouchá jen na 127.0.0.1. /state vrací JSON {seq,visible,original,translation}.
/// Styl lze měnit parametry URL: ?size=40&amp;color=ffffff&amp;bg=000000&amp;opacity=0.6&amp;font=Segoe%20UI&amp;english=1
/// </summary>
public sealed class OverlayWebServer : IDisposable
{
    TcpListener? _l; CancellationTokenSource? _cts;
    readonly object _lk = new();
    long _seq; bool _visible; string _en = "", _tr = "";
    public int Port { get; private set; }
    public bool Running => _l != null;
    public string Url => $"http://127.0.0.1:{Port}/";

    public void Update(string original, string translation, bool visible)
    {
        lock (_lk) { _seq++; _visible = visible && translation.Length > 0; _en = original; _tr = translation; }
    }
    public void Clear() => Update("", "", false);

    public string StateJson()
    {
        lock (_lk) return JsonSerializer.Serialize(new { seq = _seq, visible = _visible, original = _en, translation = _tr });
    }

    public void Start(int port)
    {
        Stop();
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        _l = l; Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); } catch { break; }
                _ = Task.Run(() => Handle(c), ct);
            }
        }, ct);
        AppLog.Info($"OBS server běží: {Url}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _l?.Stop(); } catch { }
        _l = null; _cts = null;
    }
    public void Dispose() => Stop();

    async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = c.SendTimeout = 3000;
                var ns = c.GetStream();
                var buf = new byte[4096]; int n = 0;
                while (n < buf.Length)
                {
                    int r = await ns.ReadAsync(buf.AsMemory(n)).ConfigureAwait(false);
                    if (r <= 0) break; n += r;
                    if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                }
                var line = Encoding.ASCII.GetString(buf, 0, n).Split('\r', '\n')[0];
                var parts = line.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                string clean = path.Split('?')[0];
                string body, type = "text/html; charset=utf-8"; int code = 200;
                if (parts[0] != "GET") { code = 405; body = "Method Not Allowed"; type = "text/plain"; }
                else if (clean == "/state") { body = StateJson(); type = "application/json; charset=utf-8"; }
                else if (clean == "/") body = Page;
                else { code = 404; body = "Not Found"; type = "text/plain"; }
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await ns.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                await ns.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch { }
        }
    }

    const string Page = @"<!doctype html><html><head><meta charset=""utf-8""><title>Overlingo</title>
<style>html,body{margin:0;background:transparent;overflow:hidden;height:100%}
#w{position:absolute;left:0;right:0;bottom:6%;text-align:center}
#b{display:inline-block;padding:.3em .7em;border-radius:.3em;max-width:90%;line-height:1.25;white-space:pre-wrap}
#e{opacity:.7;font-size:.7em}</style></head><body><div id=""w""><div id=""b"" style=""display:none""><div id=""e""></div><div id=""t""></div></div></div>
<script>
const q=new URLSearchParams(location.search);
const b=document.getElementById('b'),t=document.getElementById('t'),e=document.getElementById('e');
b.style.fontSize=(q.get('size')||'36')+'px';b.style.color='#'+(q.get('color')||'ffffff');
b.style.fontFamily=q.get('font')||'Segoe UI, sans-serif';
const bg=q.get('bg')||'000000',op=q.get('opacity')||'0.6';
const r=parseInt(bg.substr(0,2),16),g=parseInt(bg.substr(2,2),16),bl=parseInt(bg.substr(4,2),16);
b.style.background='rgba('+r+','+g+','+bl+','+op+')';
let last=-1;
async function tick(){try{const s=await (await fetch('state',{cache:'no-store'})).json();
if(s.seq!==last){last=s.seq;t.textContent=s.translation;e.textContent=q.get('english')==='1'?s.original:'';e.style.display=q.get('english')==='1'?'block':'none';}
b.style.display=s.visible?'inline-block':'none';}catch(x){}setTimeout(tick,150);}
tick();
</script></body></html>";
}
