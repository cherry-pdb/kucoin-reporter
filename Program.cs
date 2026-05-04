using KuCoinFuturesReporter.Data;
using KuCoinFuturesReporter.Options;
using KuCoinFuturesReporter.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KuCoinOptions>(builder.Configuration.GetSection(KuCoinOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Connection string 'Postgres' is missing.");

    options.UseNpgsql(connectionString);
});

builder.Services.AddTransient<KuCoinAuthHandler>();
builder.Services.AddHttpClient<KuCoinFuturesClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KuCoinOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<KuCoinAuthHandler>();

builder.Services.AddSingleton<TelegramReportService>();
builder.Services.AddHostedService<KuCoinSyncWorker>();

await builder.Build().RunAsync();
