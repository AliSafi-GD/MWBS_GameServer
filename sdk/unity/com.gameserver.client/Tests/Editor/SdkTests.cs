using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameServer.Client.Tests
{
    /// <summary>
    /// The SDK against a fake server (<see cref="FakeHttp"/>): no network, no Unity scene. The same file runs in the Unity
    /// Test Runner (EditMode) and with <c>dotnet test sdk/unity/SdkUnitTests</c>. The fake answers at once, so blocking
    /// on a task with GetResult is safe here.
    /// </summary>
    public class SdkTests
    {
        /// <summary>Answers requests from a table "METHOD /path" -> (status, body) and remembers what was sent.</summary>
        private sealed class FakeHttp : IHttp
        {
            public readonly Dictionary<string, (int Status, string Body)> Routes = new Dictionary<string, (int, string)>();
            public readonly List<HttpRequest> Sent = new List<HttpRequest>();

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct)
            {
                Sent.Add(request);
                var path = new Uri(request.Url).AbsolutePath;
                var key = request.Method + " " + path;
                var (status, body) = Routes.TryGetValue(key, out var route) ? route : (404, "{\"error\":\"not_found\",\"message\":\"no route " + key + "\"}");
                return Task.FromResult(new HttpResponse { Status = status, Body = body });
            }
        }

        private static (GameClient Client, FakeHttp Http) NewClient()
        {
            var http = new FakeHttp();
            var client = new GameClient(new GameClientOptions
            {
                BaseUrl = "http://test.local",
                Http = http,
                Storage = new MemoryStorage(),
                Dispatcher = new InlineDispatcher()
            });
            return (client, http);
        }

        private const string Login =
            "{\"token\":\"jwt-1\",\"refreshToken\":\"r-1\",\"userId\":\"u1\",\"username\":\"bob\",\"expiresUtc\":\"2030-01-01T00:00:00Z\"}";

        [Test]
        public void GuestLogin_StoresTheSession_AndSendsTheToken()
        {
            var (client, http) = NewClient();
            http.Routes["POST /v1/auth/device"] = (200, Login);
            http.Routes["GET /v1/wallet"] = (200, "{\"wallet\":{\"coins\":120}}");

            var login = client.Auth.LoginDeviceAsync().GetAwaiter().GetResult();
            var wallet = client.Economy.WalletAsync().GetAwaiter().GetResult();

            Assert.AreEqual("u1", login.UserId);
            Assert.IsTrue(client.Session.IsLoggedIn);
            Assert.AreEqual(120, wallet["coins"]);
            var walletCall = http.Sent.Last();
            Assert.AreEqual("Bearer jwt-1", walletCall.Headers["Authorization"]);
        }

        [Test]
        public void ServerError_BecomesApiException_WithTheCode()
        {
            var (client, http) = NewClient();
            http.Routes["POST /v1/auth/device"] = (200, Login);
            http.Routes["POST /v1/shop/pack/buy"] = (400, "{\"error\":\"insufficient_funds\",\"message\":\"Not enough coins.\"}");
            client.Auth.LoginDeviceAsync().GetAwaiter().GetResult();

            var error = Assert.Throws<ApiException>(() => client.Shop.BuyAsync("pack").GetAwaiter().GetResult());

            Assert.AreEqual("insufficient_funds", error.Code);
            Assert.AreEqual(400, error.Status);
            Assert.IsFalse(error.IsNetworkError);
        }

        [Test]
        public void Buy_SendsARequestId_SoARetryIsChargedOnce()
        {
            var (client, http) = NewClient();
            http.Routes["POST /v1/auth/device"] = (200, Login);
            http.Routes["POST /v1/shop/pack/buy"] = (200, "{\"purchase\":{\"productId\":\"pack\",\"quantity\":1,\"alreadyApplied\":false}}");
            client.Auth.LoginDeviceAsync().GetAwaiter().GetResult();

            var purchase = client.Shop.BuyAsync("pack", 1, "click-7").GetAwaiter().GetResult();

            Assert.AreEqual("pack", purchase.ProductId);
            StringAssert.Contains("\"requestId\":\"click-7\"", http.Sent.Last().Body);
        }

        [Test]
        public void Conversations_AreParsed_AndMarkReadSendsChannelAndId()
        {
            var (client, http) = NewClient();
            http.Routes["POST /v1/auth/device"] = (200, Login);
            http.Routes["GET /v1/chat/conversations"] = (200,
                "{\"items\":[{\"channel\":\"dm:u1:u2\",\"kind\":\"direct\",\"peerId\":\"u2\",\"title\":\"Ann\"," +
                "\"lastMessage\":{\"id\":42,\"channel\":\"dm:u1:u2\",\"senderId\":\"u2\",\"text\":\"hi\"},\"lastReadId\":40,\"unread\":2}]}");
            http.Routes["POST /v1/chat/read"] = (204, "");
            client.Auth.LoginDeviceAsync().GetAwaiter().GetResult();

            var list = client.Chat.ConversationsAsync().GetAwaiter().GetResult();
            client.Chat.MarkReadAsync("dm:u2", 42).GetAwaiter().GetResult();

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("u2", list[0].PeerId);
            Assert.AreEqual(2, list[0].Unread);
            Assert.AreEqual(42, list[0].LastMessage.Id);
            var body = http.Sent.Last().Body;
            StringAssert.Contains("\"channel\":\"dm:u2\"", body);
            StringAssert.Contains("\"messageId\":42", body);
        }

        [Test]
        public void TheClient_IsUsableThroughItsInterfaces()
        {
            var (client, _) = NewClient();
            IGameClient asInterface = client;

            Assert.AreSame(client.Shop, asInterface.Shop);
            Assert.IsInstanceOf<IShopApi>(client.Shop);
            Assert.IsInstanceOf<IFriendsApi>(client.Social.Friends);
            Assert.IsInstanceOf<IRealtimeClient>(client.Realtime);
        }

        // ---- game code tested with a fake API: what the interfaces are for ------------------------------------

        /// <summary>A bit of game code that only knows <see cref="IShopApi"/>.</summary>
        private sealed class CheapestProduct
        {
            private readonly IShopApi _shop;

            public CheapestProduct(IShopApi shop) => _shop = shop;

            public async Task<string> FindAsync(string currency)
            {
                var products = await _shop.ListAsync();
                return products
                    .Where(p => p.Active && p.Cost != null && p.Cost.Wallet != null && p.Cost.Wallet.ContainsKey(currency))
                    .OrderBy(p => p.Cost.Wallet[currency])
                    .Select(p => p.Id)
                    .FirstOrDefault();
            }
        }

        private sealed class FakeShop : IShopApi
        {
            public List<ShopProduct> Products = new List<ShopProduct>();

            public Task<List<ShopProduct>> ListAsync(string category = null, CancellationToken ct = default(CancellationToken)) =>
                Task.FromResult(Products);

            public Task<ShopPurchase> BuyAsync(string productId, int quantity = 1, string requestId = null, CancellationToken ct = default(CancellationToken)) =>
                Task.FromResult(new ShopPurchase { ProductId = productId, Quantity = quantity });
        }

        private static ShopProduct Product(string id, long price, bool active = true) => new ShopProduct
        {
            Id = id,
            Active = active,
            Cost = new Bundle { Wallet = new Dictionary<string, long> { ["coins"] = price } }
        };

        [Test]
        public void GameCode_CanUseAFakeShop()
        {
            var shop = new FakeShop { Products = { Product("big", 500), Product("small", 50), Product("soon", 10, active: false) } };

            var cheapest = new CheapestProduct(shop).FindAsync("coins").GetAwaiter().GetResult();

            Assert.AreEqual("small", cheapest);
        }

        // ---- small helpers --------------------------------------------------------------------------------------

        [Test]
        public void RelayMessage_SplitsTheSlotFromThePayload()
        {
            Assert.IsTrue(RelayMessage.TryParse(new byte[] { 2, 10, 20 }, out var slot, out var payload));
            Assert.AreEqual(2, slot);
            CollectionAssert.AreEqual(new byte[] { 10, 20 }, payload);
            Assert.IsFalse(RelayMessage.TryParse(new byte[0], out _, out _));
        }

        [Test]
        public void InlineDispatcher_RunsAtOnce_AndAHandlerErrorDoesNotEscape()
        {
            var log = new List<string>();
            var ran = false;
            var dispatcher = new Dispatcher(new InlineDispatcher(), log.Add);

            dispatcher.Post(() => ran = true);
            dispatcher.Post(() => throw new InvalidOperationException("boom"));

            Assert.IsTrue(ran);
            Assert.AreEqual(1, log.Count);
            StringAssert.Contains("boom", log[0]);
        }
    }
}
