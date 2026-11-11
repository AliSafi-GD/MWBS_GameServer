using GameServer.Client;
using Newtonsoft.Json.Linq;

namespace Game.Core
{
    /// <summary>What the server says about this build and its own state before the player signs in.</summary>
    public sealed class StartupCheck
    {
        public const string Ok = "ok", UpdateAvailable = "update_available", UpdateRequired = "update_required";

        public bool InMaintenance;
        public string MaintenanceMessage;
        public string MaintenanceUntil;

        public string VersionStatus = Ok;
        public string Latest;
        public string Minimum;
        public string Url;

        public bool MustUpdate => VersionStatus == UpdateRequired;
        public bool CanUpdate => VersionStatus == UpdateAvailable;

        /// <summary>Reads the public "maintenance" config key: { "enabled": true, "message": "...", "until": "..." }.</summary>
        public static StartupCheck FromConfig(System.Collections.Generic.IDictionary<string, JToken> config)
        {
            var check = new StartupCheck();
            if (config != null && config.TryGetValue("maintenance", out var token) && token is JObject m && (bool?)m["enabled"] == true)
            {
                check.InMaintenance = true;
                check.MaintenanceMessage = (string)m["message"];
                check.MaintenanceUntil = (string)m["until"];
            }

            return check;
        }

        public void ApplyVersion(VersionCheck v)
        {
            VersionStatus = v?.Status ?? Ok;
            Latest = v?.Latest;
            Minimum = v?.Minimum;
            Url = v?.Url;
        }
    }
}
