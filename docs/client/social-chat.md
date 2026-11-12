# Social and chat

## Friends

```csharp
var friends  = await client.Social.Friends.ListAsync("friend");           // friend | invite_received | invite_sent | blocked
await client.Social.Friends.AddAsync(userIdOrUsername);                   // sends an invite, or accepts a received one
await client.Social.Friends.RemoveAsync(userId);
await client.Social.Friends.BlockAsync(userId);
await client.Social.Friends.UnblockAsync(userId);
var online = await client.Social.Friends.OnlineAsync(ids);
```

Errors: `not_friends`, `friend_limit`. A new request arrives as the realtime event `friend.request`.

## Clans (groups)

```csharp
var g = await client.Social.Groups.CreateAsync(new NewGroup { Name = "Tanks", Type = "clan" });
var found = await client.Social.Groups.SearchAsync("tank", "clan");
var mine  = await client.Social.Groups.MineAsync();
await client.Social.Groups.JoinAsync(id);
await client.Social.Groups.LeaveAsync(id);
var members = await client.Social.Groups.MembersAsync(id);
```

Management: `InviteAsync`, `AcceptAsync`, `KickAsync`, `BanAsync`, `UnbanAsync`, `PromoteAsync`, `DemoteAsync`,
`TransferAsync`, `UpdateAsync`, `DeleteAsync`. Errors: `not_group_admin`, `group_full`, `banned_from_group`.

## Parties

```csharp
var party = await client.Social.Parties.CreateAsync(open: true, maxSize: 4);
await client.Social.Parties.InviteAsync(party.Id, userId);
await client.Social.Parties.JoinAsync(partyId);
await client.Social.Parties.LeaveAsync(partyId);
// also: MineAsync, GetAsync, KickAsync, PromoteAsync, CloseAsync
client.Realtime.PartyInvited += p => ...;
client.Realtime.PartyUpdated += p => ...;
client.Realtime.PartyLeft   += p => ...;
```

A party needs an open realtime connection. The leader queues the whole party: `AddToMatchmakerAsync(mode, props, partyId)`.

## Chat

Channels are made with `Channels`:

```csharp
Channels.Room("lobby")        // room:lobby
Channels.Group(clanId)        // group:<id>
Channels.Party(partyId)       // party:<id>
Channels.Direct(userId)       // dm:<userId>
```

```csharp
var rt = client.Realtime;
var ch = await rt.JoinChannelAsync(Channels.Room("lobby"));
await rt.SendMessageAsync(ch, "hi");

rt.ChatMessageReceived += m => Debug.Log($"{m.SenderName}: {m.Text}");
rt.ChatMessageDeleted  += (channel, id) => RemoveBubble(id);   // (string channel, long messageId)

// newest first; pass the oldest id you have as "before" to load the next page
var history = await client.Chat.HistoryAsync(Channels.Direct(friendId), limit: 40, before: null);
```

::: info What is stored
Direct and clan messages are stored (30 days by default). Party messages never. Room messages only if the server has
`GameServer:Chat:PersistRooms` on. See [Chat and realtime](../server/realtime).
:::

## Reference implementation

The Tank Arena client has a complete chat service: `samples/unity-client/Assets/Scripts/Core/Services/ChatService.cs`
(one `ChatThread` per conversation, unread counts, toasts for messages not on screen) and `SocialServices.cs`.
