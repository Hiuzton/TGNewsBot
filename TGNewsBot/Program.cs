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
using static System.Net.WebRequestMethods;

class Program
{
    private static string botToken;
    private static string newsApiKey;
    private static string weatherApiKey;
    private static string quotesApiUrl;
    private static string weatherApiUrl;
    private static TelegramBotClient botClient;
    private static long chatID;
    private static readonly int hour = 9;
    private static readonly int minutes = 00;
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

        botToken = "TELEGRAM_API_KEY";
        chatID = 723491344;
        newsApiKey = "NEWS_API_KEY";
        weatherApiKey = "WEATHER_API_KEY";
        quotesApiUrl = "https://zenquotes.io/api/random";
        weatherApiUrl = "https://api.openweathermap.org/data/2.5/weather?q={0}&units=metric&appid=" + weatherApiKey;

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
            Console.WriteLine("Job executed at: " + DateTime.Now);
            await SendMorningMessage(botClient, chatID);
            await SendMessageWithCountriesToChoose(botClient, chatID);
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
            await OnMessageSent(bot, update);

        if (update.CallbackQuery != null)
            await OnQoueryCallback(update);
    }

    private static async Task OnMessageSent(ITelegramBotClient bot, Update update)
    {
        long chatId = update.Message.Chat.Id;

        if (update.Message.Text == "/start")
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                    new KeyboardButton[] { "📰 News", "⛅ Weather" }
                })
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = false
            };

            await bot.SendTextMessageAsync(
                chatId,
                "Welcome to Morning Bastard!\nThis bot provides morning updates with news and weather.",
                replyMarkup: keyboard
            );
        }
        else if (update.Message.Text == "📰 News")
        {
            await SendMessageWithCountriesToChoose(bot, chatId);
        }
        else if (update.Message.Text == "⛅ Weather")
        {
            await bot.SendTextMessageAsync(chatId, await GetWeather("Sibiu"));
        }
    }

    private static async Task OnQoueryCallback(Update update)
    {
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

    private static async Task SendMorningMessage(ITelegramBotClient bot, long chatId)
    {
        string quote = await GetMotivationalQuote();
        string weather = await GetWeather("sibiu");
        string messageToSent = $"Morning dude.\nHope you are fine today.\n" +
            $"For your good being: {quote}\n" +
            $"Weather for today {weather}\n" +
            $"For the latest news choose the country below:";

        await bot.SendTextMessageAsync(chatId, messageToSent);
    }

    private static async Task<string> GetMotivationalQuote()
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
    private static async Task<string> GetWeather(string city)
    {
        string url = string.Format(weatherApiUrl, city);
        using HttpClient client = new();

        HttpResponseMessage response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to fetch weather. Status Code: {response.StatusCode}");
            return "";
        }

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(content);

        var weather = doc.RootElement.GetProperty("weather")[0].GetProperty("description").GetString();
        var temp = doc.RootElement.GetProperty("main").GetProperty("temp").GetDouble();
        var tempMin = doc.RootElement.GetProperty("main").GetProperty("temp_min").GetDouble();
        var tempMax = doc.RootElement.GetProperty("main").GetProperty("temp_max").GetDouble();
        var humidity = doc.RootElement.GetProperty("main").GetProperty("humidity").GetInt32();
        var windSpeed = doc.RootElement.GetProperty("wind").GetProperty("speed").GetDouble();

        string message = $"🌦️ Weather in {city}:\n" +
                         $"🌡️ *Temperature:* {temp}°C\n" +
                         $"🔽 *Min Temp:* {tempMin}°C\n" +
                         $"🔼 *Max Temp:* {tempMax}°C\n" +
                         $"💨 *Wind Speed:* {windSpeed} m/s\n" +
                         $"💧 *Humidity:* {humidity}%\n" +
                         $"📌 *Condition:* {weather}";

        return message;
    }

    private static async Task SendMessageWithCountriesToChoose(ITelegramBotClient bot, long chatId)
    {
        var inlineKeyboard = new List<List<InlineKeyboardButton>>();

        foreach (var country in countries.Keys)
        {
            inlineKeyboard.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData(country, $"country_{country}")
                });
        }

        var replyMarkup = new InlineKeyboardMarkup(inlineKeyboard);
        await bot.SendTextMessageAsync(chatId,"Choose the country", replyMarkup: replyMarkup);
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
