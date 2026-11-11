using System.Collections.Generic;
using System.Threading.Tasks;
using GameServer.Client;
#if GAMESERVER_LITENETLIB
using GameServer.Client.Udp; // only exists after you add LiteNetLib and the GAMESERVER_LITENETLIB define (see README)
#endif
using UnityEngine;

/// <summary>
/// Put this on a GameObject. It signs in as a guest, opens the live connection, joins a chat room, queues for a
/// match and plays it. Replace the mode name ("duel") with one from your server's "matches" config.
/// </summary>
public class QuickStart : MonoBehaviour
{
    public string serverUrl = "http://localhost:5000";
    public string mode = "duel";

    private GameClient _client;
    private MatchConnection _match;

    private async void Start()
    {
        var options = new GameClientOptions { BaseUrl = serverUrl, Log = Debug.Log };
#if GAMESERVER_LITENETLIB
        options.UseLiteNetLib(); // lets the client join matches whose mode uses the "udp" transport
#endif
        _client = new GameClient(options);

        // Continue the saved session, or sign in as a guest on first launch.
        if (!await _client.Auth.TryRestoreSessionAsync())
            await _client.Auth.LoginDeviceAsync();
        Debug.Log("Signed in as " + _client.Session.UserId);

        var wallet = await _client.Economy.WalletAsync();
        foreach (var coin in wallet)
            Debug.Log(coin.Key + ": " + coin.Value);

        // Events are raised on the main thread, so they can touch GameObjects directly.
        _client.Realtime.ChatMessageReceived += message => Debug.Log("[chat] " + message.SenderName + ": " + message.Text);
        _client.Realtime.MatchFound += OnMatchFound;
        _client.Realtime.NotificationReceived += note => Debug.Log("New notification: " + note.Subject);
        _client.Realtime.On("friend.request", data => Debug.Log("Friend request: " + data));
        _client.Realtime.Disconnected += reason => Debug.Log("Disconnected: " + reason);

        await _client.Realtime.ConnectAsync();
        var room = await _client.Realtime.JoinChannelAsync(Channels.Room("lobby"));
        await _client.Realtime.SendMessageAsync(room, "Hello from Unity!");

        // Ask the matchmaker for a game. The answer arrives as MatchFound.
        await _client.Realtime.AddToMatchmakerAsync(mode, new Dictionary<string, object> { { "region", "eu" } });
        Debug.Log("Waiting for an opponent...");
    }

    private async void OnMatchFound(MatchFound found)
    {
        Debug.Log("Match found: " + found.MatchId + " over " + found.Transport);

        // The protocol (websocket or udp) is chosen by the server per game mode; the SDK follows it.
        _match = await _client.Matches.ConnectAsync(found);
        _match.DataReceived += data =>
        {
            int slot;
            byte[] payload;
            if (RelayMessage.TryParse(data, out slot, out payload))
                Debug.Log("Player in slot " + slot + " sent " + payload.Length + " bytes");
        };
        _match.Closed += reason => Debug.Log("Match closed: " + reason);

        await _match.SendAsync(new byte[] { 1, 2, 3 });
    }

    private async void OnDestroy()
    {
        if (_match != null)
            await _match.CloseAsync();
        if (_client != null)
            await _client.Realtime.DisconnectAsync();
    }
}
