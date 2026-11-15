using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // ---- account ----------------------------------------------------------------------------------------------

    public class IdentityInfo
    {
        public string Provider { get; set; }
        public string Label { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class Account
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public string Lang { get; set; }
        public string Timezone { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public List<IdentityInfo> Identities { get; set; }
    }

    /// <summary>Send only the fields you want to change.</summary>
    public class ProfileUpdate
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public string Lang { get; set; }
        public string Timezone { get; set; }
    }

    public class PublicProfile
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class VersionCheck
    {
        /// <summary>"ok", "update_available" or "update_required".</summary>
        public string Status { get; set; }
        public string Latest { get; set; }
        public string Minimum { get; set; }
        public string Url { get; set; }
    }
}
