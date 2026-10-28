using System.Text;
using System.Text.Json;

namespace YuhuAdminBle;

/// <summary>
/// AdminClient (A.3) — the ADMIN (laptop) side of the challenge handshake and
/// log protocol. Plays opposite the Kotlin AdminService:
///
///   → ADMIN_HELLO { cert }
///   ← ADMIN_CHALLENGE { nonce }
///   → ADMIN_PROOF { sig = sign(nonce ‖ phoneLogicalId) }
///   ← ADMIN_OK
///   → LOG_TAIL / LOG_LIVE / PING
///   ← LOG / LIVE / PONG
///
/// Transport-agnostic: caller supplies a send delegate and pumps inbound admin
/// JSON envelopes via <see cref="OnEnvelope"/>. This keeps it unit-testable
/// off-device and lets the same client drive either the WinRT transport or a
/// test harness.
/// </summary>
public sealed class AdminClient
{
    public enum State { Idle, HelloSent, ProofSent, Authed, Failed }

    private readonly AdminIdentity _identity;
    private readonly Action<string> _send;         // send an admin JSON envelope
    private readonly string _phoneLogicalId;        // the target phone's 16-hex id

    public State Current { get; private set; } = State.Idle;

    /// <summary>Raised when the link is authenticated (ADMIN_OK received).</summary>
    public event Action? Authenticated;
    /// <summary>Raised for each batch of tailed log lines.</summary>
    public event Action<IReadOnlyList<string>>? LogBatch;
    /// <summary>Raised for each live log line.</summary>
    public event Action<string>? LiveLine;
    /// <summary>Raised on handshake failure.</summary>
    public event Action<string>? Failed_;

    public AdminClient(AdminIdentity identity, string phoneLogicalId, Action<string> send)
    {
        _identity = identity;
        _phoneLogicalId = phoneLogicalId;
        _send = send;
    }

    /// <summary>Kick off the handshake by sending ADMIN_HELLO with the cert.</summary>
    public void Start()
    {
        var hello = new Dictionary<string, object>
        {
            ["t"] = "ADMIN_HELLO",
            ["cert"] = JsonSerializer.Deserialize<JsonElement>(_identity.CertJson),
        };
        _send(JsonSerializer.Serialize(hello));
        Current = State.HelloSent;
    }

    /// <summary>Feed a decoded admin JSON envelope received from the phone.</summary>
    public void OnEnvelope(string json)
    {
        JsonElement o;
        try { o = JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return; }
        if (!o.TryGetProperty("t", out var tEl)) return;
        var t = tEl.GetString();

        switch (t)
        {
            case "ADMIN_CHALLENGE":
                HandleChallenge(o);
                break;
            case "ADMIN_OK":
                Current = State.Authed;
                Authenticated?.Invoke();
                break;
            case "LOG":
                if (o.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    LogBatch?.Invoke(lines.EnumerateArray().Select(e => e.GetString() ?? "").ToList());
                break;
            case "LIVE":
                if (o.TryGetProperty("line", out var line))
                    LiveLine?.Invoke(line.GetString() ?? "");
                break;
            case "PONG":
                // liveness ack — no-op here
                break;
            default:
                break;
        }
    }

    private void HandleChallenge(JsonElement o)
    {
        if (Current != State.HelloSent) return;
        if (!o.TryGetProperty("nonce", out var nonceEl)) { Fail("challenge missing nonce"); return; }
        var nonce = Convert.FromBase64String(nonceEl.GetString() ?? "");

        // Sign nonce ‖ phoneLogicalId — binds the proof to THIS phone (replay guard).
        var message = new byte[nonce.Length + Encoding.UTF8.GetByteCount(_phoneLogicalId)];
        Buffer.BlockCopy(nonce, 0, message, 0, nonce.Length);
        Encoding.UTF8.GetBytes(_phoneLogicalId, 0, _phoneLogicalId.Length, message, nonce.Length);

        var sig = _identity.Sign(message);
        var proof = new Dictionary<string, object>
        {
            ["t"] = "ADMIN_PROOF",
            ["sig"] = Convert.ToBase64String(sig),
        };
        _send(JsonSerializer.Serialize(proof));
        Current = State.ProofSent;
    }

    private void Fail(string reason)
    {
        Current = State.Failed;
        Failed_?.Invoke(reason);
    }

    // ---- post-auth commands ----

    public void RequestLogTail(string? tag = null, int lines = 200)
    {
        RequireAuth();
        var req = new Dictionary<string, object> { ["t"] = "LOG_TAIL", ["lines"] = lines };
        if (tag != null) req["tag"] = tag;
        _send(JsonSerializer.Serialize(req));
    }

    public void SetLiveLogs(bool on)
    {
        RequireAuth();
        _send(JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "LOG_LIVE", ["on"] = on }));
    }

    public void Ping()
    {
        RequireAuth();
        _send(JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "PING" }));
    }

    private void RequireAuth()
    {
        if (Current != State.Authed)
            throw new InvalidOperationException("admin link not authenticated");
    }
}
