using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>One message from the server: a reply to a request (has an id) or a pushed event.</summary>
    public sealed class RealtimeMessage
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public JToken Data { get; set; }
    }

    /// <summary>The event "notification.new": a new inbox message arrived (read it with <see cref="NotificationsApi"/>).</summary>
    public sealed class NotificationEvent
    {
        public long Id { get; set; }
        public bool Broadcast { get; set; }
        public string Subject { get; set; }
        public bool HasReward { get; set; }
    }

    public sealed class PartyInvite
    {
        public string PartyId { get; set; }
        /// <summary>Who invited you (shape as sent by the server).</summary>
        public JToken From { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }

    public sealed class PartyLeft
    {
        public string PartyId { get; set; }
        public string Reason { get; set; }
    }
}
