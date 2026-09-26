using Microsoft.Extensions.Logging;

namespace PhotoFrame
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureMauiHandlers(handlers =>
                    // Своя обёртка над системным VideoView вместо MediaElement из
                    // CommunityToolkit: меньше зависимостей на 32-битной рамке.
                    handlers.AddHandler<VideoPlayerView, VideoPlayerViewHandler>())
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Singleton: внутри SharedAlbumPhotoSource живёт настроенный HttpClient,
            // пересоздавать его на каждый показ страницы не нужно.
            builder.Services.AddSingleton<SharedAlbumPhotoSource>();
            builder.Services.AddSingleton<ImmichPhotoSource>();
            builder.Services.AddSingleton<LocalFolderPhotoSource>();
            builder.Services.AddSingleton<CompositePhotoSource>();
            builder.Services.AddSingleton<HomeAssistantClient>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
