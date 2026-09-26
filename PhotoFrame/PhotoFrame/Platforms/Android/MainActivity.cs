using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PhotoFrame
{
    /// <summary>
    /// Экран слайд-шоу и заодно домашний экран рамки.
    /// </summary>
    /// <remarks>
    /// Категория HOME делает приложение домашним экраном рамки, поэтому показ
    /// открывается сразу при включении. Своего лаунчера в прошивке нет — его роль
    /// играет предустановленный Frameo, который вдобавок переставляет часовой пояс
    /// на свой и занимает заметную часть памяти устройства. С этой категорией Frameo
    /// можно отключить, ничего не потеряв.
    /// </remarks>
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(
        new[] { Intent.ActionMain },
        Categories = new[] { Intent.CategoryHome, Intent.CategoryDefault })]
    public class MainActivity : MauiAppCompatActivity
    {
        /// <summary>Пакет предустановленного Frameo — тот самый лаунчер, что переставляет пояс.</summary>
        private const string FrameoPackage = "net.frameo.frame";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            KillFrameoIfRunning();
        }

        /// <summary>
        /// Frameo поднимается фоном и спустя секунды после старта переставляет часовой
        /// пояс на свой, теряя ручную правку. Убиваем его при каждом своём запуске.
        /// </summary>
        private void KillFrameoIfRunning()
        {
            try
            {
                if (GetSystemService(ActivityService) is ActivityManager manager)
                {
                    manager.KillBackgroundProcesses(FrameoPackage);
                }
            }
            catch (Java.Lang.Throwable killFailure)
            {
                // Пакета может не быть на прошивке вовсе — тогда и убивать нечего.
                FrameLog.Warn($"Не удалось остановить Frameo: {killFailure.Message}");
            }
        }
    }
}
