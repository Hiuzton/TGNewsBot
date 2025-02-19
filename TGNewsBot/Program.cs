using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Quartz;
using Quartz.Impl;
using TGNewsBot.Models;

class Program
{
    private static string botToken;
    private static string newsApiKey;
    private static string quotesApiUrl;
    private static TelegramBotClient botClient;
    private static long chatID;
    private static readonly int hour = 21;
    private static readonly int minutes = 38;
    private static List<News> newsList = new List<News>();
    private static readonly Dictionary<string, string> countries = new()
    {
        { "Romania", "romania" },
        { "Moldova", "moldova" },
        { "USA", "us" },
        { "Ucraine", "ucraine" },
        { "Germany", "germany" },
        { "Russian", "russian" },
    };

    static async Task Main()
    {
        var host = CreateHostBuilder().Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();

        botToken = configuration["TelegramBot:Token"];
        chatID = Convert.ToInt64(configuration["TelegramBot:ChatID"]);
        newsApiKey = configuration["NewsApi:ApiKey"];
        quotesApiUrl = configuration["QuotesApi:Url"];

        botClient = new TelegramBotClient(botToken);
        botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync);

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
            // Ensure that the news has been fetched before sending
            if (newsList.Count == 0)
            {
                Console.WriteLine("No news available. Skipping the job.");
                return;
            }

            Console.WriteLine("Job executed at: " + DateTime.Now);
            await SendNewsList(chatID, 7);
        }
    }

    private static async Task GetNews(string countryCode)
    {
        string url = $"https://newsapi.org/v2/everything?q={countryCode}&apiKey={newsApiKey}";
        using HttpClient client = new();
        client.DefaultRequestHeaders.Add("User-Agent", "MyTelegramBot/1.0");

        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to fetch news. Status Code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            return;
        }

        string responseContent = await response.Content.ReadAsStringAsync();
        Console.WriteLine("Response Content: " + responseContent);

        JsonDocument doc = JsonDocument.Parse(responseContent);

        if (!doc.RootElement.TryGetProperty("articles", out var articles))
        {
            Console.WriteLine("No articles found in the response.");
            return;
        }

        if (articles.GetArrayLength() == 0)
        {
            Console.WriteLine("No articles found.");
            return;
        }

        newsList.Clear();
        for (int i = 0; i < articles.GetArrayLength(); i++)
        {
            newsList.Add(new News
            {
                Id = i + 1,
                Title = articles[i].GetProperty("title").GetString(),
                Description = articles[i].GetProperty("description").GetString(),
                Url = articles[i].GetProperty("url").GetString(),
                ImageUrl = articles[i].GetProperty("urlToImage").GetString()
            });
        }
    }

    private static async Task SendNewsList(long chatId, int offset = 0)
    {
        if (newsList.Count == 0)
        {
            await botClient.SendTextMessageAsync(chatId, "❌ No news available.");
            return;
        }

        int batchSize = 7;
        var newsBatch = newsList.Skip(offset).Take(batchSize).ToList();

        var inlineKeyboard = new List<List<InlineKeyboardButton>>();

        foreach (var news in newsBatch)
        {
            inlineKeyboard.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData(news.Title, $"news_{news.Id}")
            });
        }

        if (offset + batchSize < newsList.Count)
        {
            inlineKeyboard.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("➡ More News", $"more_{offset + batchSize}")
            });
        }

        var replyMarkup = new InlineKeyboardMarkup(inlineKeyboard);
        await botClient.SendTextMessageAsync(chatId, "📰 Latest News:", replyMarkup: replyMarkup);
    }

    private static async Task SendNewsDetails(long chatId, int newsId)
    {
        var newsItem = newsList.FirstOrDefault(n => n.Id == newsId);
        if (newsItem == null)
        {
            await botClient.SendTextMessageAsync(chatId, "❌ News item not found.");
            return;
        }

        var messageText = $"📰 *{newsItem.Title}*\n\n{newsItem.Description}\n\n🔗 [Read More]({newsItem.Url})";
        await botClient.SendPhotoAsync(
            chatId,
            InputFile.FromUri(newsItem.ImageUrl),
            caption: messageText,
            parseMode: ParseMode.Markdown
        );
    }
    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        if (update.Message != null)
        {
            long chatId = update.Message.Chat.Id;

            if (update.Message.Text == "/start" || update.Message.Text == "🆕 Start")
            {
                var keyboard = new ReplyKeyboardMarkup(new[]
                {
                    new KeyboardButton[] { "🆕 Start" }, 
                    new KeyboardButton[] { "News" } // Button appears under the input
                })
                {
                    ResizeKeyboard = true, // Makes it compact
                    OneTimeKeyboard = false // Keeps it visible
                };


                await bot.SendTextMessageAsync(
                    chatId,
                    "Welcome! Click the button below to get started:",
                    replyMarkup: keyboard
                );
                var inlineKeyboard = new List<List<InlineKeyboardButton>>();

                foreach (var country in countries.Keys)
                {
                    inlineKeyboard.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData(country, $"country_{country}")
                });
                }

                var replyMarkup = new InlineKeyboardMarkup(inlineKeyboard);
                await bot.SendTextMessageAsync(chatId, "Please choose a country to get news from:", replyMarkup: replyMarkup);
            }
        }

        if (update.CallbackQuery != null)
        {
            string data = update.CallbackQuery.Data;
            long chatId = update.CallbackQuery.Message.Chat.Id;

            if (data.StartsWith("country_"))
            {
                string countryName = data.Split('_')[1];
                if (countries.ContainsKey(countryName))
                {
                    string countryCode = countries[countryName];
                    await GetNews(countryCode);
                    await SendNewsList(chatId);
                }
            }
            else if (data.StartsWith("news_"))
            {
                int newsId = int.Parse(data.Split('_')[1]);
                await SendNewsDetails(chatId, newsId);
            }
            else if (data.StartsWith("more_"))
            {
                int offset = int.Parse(data.Split('_')[1]);
                await SendNewsList(chatId, offset);
            }
        }
    }

    private static Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"Error: {exception.Message}");
        return Task.CompletedTask;
    }

    public static IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<IConfiguration>(hostContext.Configuration);
            });
}
