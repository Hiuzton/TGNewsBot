using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Quartz;
using Quartz.Impl;

class Program
{
    private static string botToken;
    private static string newsApiKey;
    private static string quotesApiUrl;
    private static TelegramBotClient botClient;
    private static long chatID;
    private static readonly int hour = 22;
    private static readonly int minutes = 55;

    static async Task Main()
    {
        var host = CreateHostBuilder().Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();

        botToken = configuration["TelegramBot:Token"];
        chatID = Convert.ToInt64(configuration["TelegramBot:ChatID"]);
        newsApiKey = configuration["NewsApi:ApiKey"];
        quotesApiUrl = configuration["QuotesApi:Url"];

        botClient = new TelegramBotClient(botToken);

        Console.WriteLine("Bot is starting...");

        StdSchedulerFactory factory = new();
        IScheduler scheduler = await factory.GetScheduler();
        await scheduler.Start();

        IJobDetail job = JobBuilder.Create<SendNewsJob>().Build();
        ITrigger trigger = TriggerBuilder.Create()
            .WithDailyTimeIntervalSchedule(s => s
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(hour, minutes))
                .WithIntervalInHours(24))
            .Build();

        await scheduler.ScheduleJob(job, trigger);
        Console.WriteLine("Scheduled daily news updates at 10:00 AM.");

        await Task.Delay(-1);
    }

    public class SendNewsJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            Console.WriteLine("Job executed at: " + DateTime.Now);
            long chatId = chatID;
            string newsAndQuote = await GetNewsAndQuote();
            await botClient.SendTextMessageAsync(chatId, newsAndQuote);
        }

        private async Task<string> GetNewsAndQuote()
        {
            string news = await GetNews();
            string quote = await GetMotivationalQuote();
            return $"{news}\n\nMotivational Quote:\n{quote}";
        }

        private async Task<string> GetNews()
        {
            string url = $"https://newsapi.org/v2/top-headlines?country=us&apiKey={newsApiKey}";
            using HttpClient client = new();

            client.DefaultRequestHeaders.Add("User-Agent", "MyTelegramBot/1.0");

            HttpResponseMessage response = await client.GetAsync(url);
            string responseContent = await response.Content.ReadAsStringAsync();
            JsonDocument doc = JsonDocument.Parse(responseContent);
            var articles = doc.RootElement.GetProperty("articles");

            var newsString = new StringBuilder();
            for (int i = 0; i < 5; i++)
            {
                newsString.AppendLine($"{i + 1}. {articles[i].GetProperty("title").GetString()}");
            }

            return newsString.ToString();
        }

        private async Task<string> GetMotivationalQuote()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(quotesApiUrl);
            string responseContent = await response.Content.ReadAsStringAsync();

            JsonDocument doc = JsonDocument.Parse(responseContent);

            JsonElement quoteElement = doc.RootElement[0];

            string quote = quoteElement.GetProperty("q").GetString();
            string author = quoteElement.GetProperty("a").GetString();

            return $"\"{quote}\" — {author}";
        }
    }

    public static IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<IConfiguration>(hostContext.Configuration);
            });
}
