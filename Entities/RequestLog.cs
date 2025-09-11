namespace honey_badger_api.Entities
{
    public class RequestLog
    {
        public long Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public int DurationMs { get; set; }

        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public string? Query { get; set; }
        public int StatusCode { get; set; }

        public string? UserId { get; set; }     // Identity user (if any)
        public string? Email { get; set; }

        public string Ip { get; set; } = "";
        public string? Country { get; set; }    // set by proxy/CDN or future geo
        public string? Asn { get; set; }        // optional (future)

        public string? UserAgent { get; set; }
        public string? UaFamily { get; set; }   // quick UA parse bucket
        public bool IsBot { get; set; }
        public string? Referrer { get; set; }

        public string? Protocol { get; set; }   // HTTP/1.1, h2
        public string? TlsProtocol { get; set; } // TLS1.2/1.3 if available
        public string? TlsCipher { get; set; }   // if available

        public long? ResponseBytes { get; set; }

        public bool Blocked { get; set; }        // was blocked by guard?
        public string? BlockReason { get; set; } // e.g. ip-ban, waf
        public string? WafFlagsJson { get; set; } // rule hits summary
        public string? ExtraJson { get; set; }     // free-form for experiments
    }
}
