using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GameServer.Client
{
    /// <summary>JSON settings shared by the whole SDK. The server speaks camelCase; dictionary keys are never renamed.</summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true
                }
            },
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTime,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        /// <summary>Converts a token (for example the data of a realtime event) to a typed object.</summary>
        public static T To<T>(this JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return default(T);
            return token.ToObject<T>(Serializer);
        }

        /// <summary>Reads one property of a response object, e.g. <c>root.Get&lt;List&lt;Foo&gt;&gt;("foos")</c>.</summary>
        public static T Get<T>(this JObject root, string name)
        {
            if (root == null)
                return default(T);
            var token = root[name];
            return token == null ? default(T) : token.To<T>();
        }
    }
}
