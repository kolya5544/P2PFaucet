using Bybit.P2P;
using Bybit.P2P.Models;
using SixLabors.ImageSharp;
using SixLaborsCaptcha.Core;
using System.Security.Cryptography;
using System.Text;

namespace P2PFaucet
{
    internal class Program
    {
        public static string API_KEY = Environment.GetEnvironmentVariable("API_KEY");
        public static string API_SECRET = Environment.GetEnvironmentVariable("API_SECRET");
        public static string SALT = Environment.GetEnvironmentVariable("SALT");
        public static bool TESTNET = Environment.GetEnvironmentVariable("TESTNET") == "true";

        public static APIClient client = new APIClient(API_KEY, API_SECRET, testnet: TESTNET);

        static async Task Main(string[] args)
        {
            var slc = new SixLaborsCaptchaModule(new SixLaborsCaptchaOptions
            {
                DrawLines = 5,
                TextColor = new[] { Color.Red },
                DrawLinesColor = new[] { Color.Gray, Color.Black, Color.DarkGrey, Color.SlateGray },
            });

            Directory.CreateDirectory("captcha");

            Console.WriteLine("Init success");

            while (true)
            {
                try
                {
                    // get all pending orders
                    var pending = await GetAllPendingOrders();
                    Console.WriteLine($"A total of {pending.Count} pending orders acquired.");

                    // for every pending order that is awaiting coin release, get last 10 chat messages
                    foreach (var order in pending)
                    {
                        // if the order is older than 1 day, ignore it completely
                        var ts = long.Parse(order.CreateDate);
                        if (DateTimeOffset.FromUnixTimeMilliseconds(ts) - DateTimeOffset.UtcNow > TimeSpan.FromDays(1)) continue;

                        var messages = await client.GetChatMessages(new
                        {
                            orderId = order.Id,
                            size = "10"
                        });

                        // if all 10 messages are by the other party, ignore it
                        if (messages.Messages.Count == 10)
                        {
                            if (messages.Messages.All(m => m.UserId == order.TargetUserId)) continue;
                        }

                        var captchaText = GetCaptchaText(order.Id);

                        Console.WriteLine($"Processing {order.Id}... Captcha = {captchaText}");

                        // check if the solved captcha has been sent to the chat
                        var captchaMsg = messages.Messages.FirstOrDefault(m => m.UserId == order.TargetUserId && m.Message == captchaText);
                        if (captchaMsg is not null)
                        {
                            // hooray!
                            // release the assets

                            await client.ReleaseAssets(
                                new
                                {
                                    orderId = order.Id
                                }
                            );

                            Console.WriteLine($"Releasing assets for {order.Id}");
                            continue;
                        }

                        // ...otherwise, check if we have already sent the captcha
                        var sent = messages.Messages.Any(m => m.UserId == order.UserId && m.ContentType == "pic");

                        if (!sent)
                        {
                            // send a captcha message
                            var captcha = slc.Generate(captchaText);

                            // save captcha to a local storage
                            var fn = $"captcha/c_{order.TargetUserId}.png";
                            File.WriteAllBytes(fn, captcha);

                            var uploadFile = await client.UploadChatFile(
                                new
                                {
                                    upload_file = fn
                                }
                            );

                            // send the captcha to the chat
                            await client.SendChatMessage(
                                new
                                {
                                    message = "Welcome to P2P Faucet! To get your funds, you need to solve this simple captcha. Good luck!",
                                    contentType = "txt",
                                    orderId = order.Id,
                                    msgUuid = Guid.NewGuid().ToString(),
                                }
                            );

                            await client.SendChatMessage(
                                new
                                {
                                    message = uploadFile.Url,
                                    contentType = "pic",
                                    orderId = order.Id,
                                    msgUuid = Guid.NewGuid().ToString(),
                                }
                            );
                        }
                    }

                    Console.WriteLine("Waiting...");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    await Task.Delay(30000); // extend the wait just in case it's a timeout or a ratelimit
                }
                await Task.Delay(5000);
            }
        }

        public static string GetCaptchaText(string oid)
        {
            // sha256
            using (var s = SHA256.Create())
            {
                var hash = s.ComputeHash(Encoding.UTF8.GetBytes($"{oid}{SALT}"));
                return BitConverter.ToString(hash).Replace("-", "").ToLower().Substring(0, 6);
            }
        }

        // get all pending orders
        public static async Task<List<GetOrdersResponse.Item>> GetAllPendingOrders()
        {
            int page = 1;
            int size = 10;

            List<GetOrdersResponse.Item> result = new();

            while (true)
            {
                var orders = await client.GetPendingOrders(new
                {
                    page = page,
                    size = size,
                    status = 20,
                    side = 1
                });

                result.AddRange(orders.Items);

                if (orders.Count <= page * size) break;
                page++;
            }

            return result;
        }
    }
}
