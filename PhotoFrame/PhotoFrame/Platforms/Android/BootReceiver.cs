using Android.App;
using Android.Content;
using Android.OS;

namespace PhotoFrame
{
    /// <summary>
    /// Открывает слайд-шоу после включения рамки.
    /// </summary>
    /// <remarks>
    /// Рамка стоит на полке и включается кнопкой питания или после отключения света —
    /// запускать приложение вручную с пульта лаунчера неудобно.
    ///
    /// Запуск отложен на несколько секунд: сразу после загрузки система ещё поднимает
    /// свой лаунчер, и активность, показанная в этот момент, оказывается перекрыта им.
    /// Ожидание отдано AlarmManager, а не задержке в самом получателе: OnReceive обязан
    /// вернуть управление за считанные секунды, а процесс после этого могут выгрузить.
    /// </remarks>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[]
    {
        Intent.ActionBootCompleted,

        // Быстрая загрузка на части прошивок рассылает своё событие вместо BOOT_COMPLETED.
        "android.intent.action.QUICKBOOT_POWERON",
    })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            // Записи в журнал, а не Debug.WriteLine: в сборке Release тот вырезается,
            // и по нему не судить, дошло ли событие вообще.
            FrameLog.Info($"Получено событие загрузки: {intent?.Action}");

            if (context is null || !FrameSettings.LaunchOnBoot)
            {
                FrameLog.Info("Запуск после загрузки выключен в настройках.");
                return;
            }

            try
            {
                int delaySeconds = FrameSettings.LaunchOnBootDelaySeconds;
                ScheduleLaunch(context, delaySeconds);
                FrameLog.Info($"Запуск назначен через {delaySeconds} с.");
            }
            catch (Java.Lang.Throwable launchFailure)
            {
                // Не удалось — рамка просто останется на лаунчере.
                FrameLog.Warn($"Запуск после загрузки не назначен: {launchFailure.Message}");
            }
        }

        private static void ScheduleLaunch(Context context, int delaySeconds)
        {
            var launchIntent = new Intent(context, typeof(MainActivity));
            launchIntent.AddFlags(ActivityFlags.NewTask);

            // Immutable: систему не нужно пускать в наш Intent, а на API 31+ флаг обязателен.
            PendingIntent? pendingLaunch = PendingIntent.GetActivity(
                context,
                requestCode: 0,
                launchIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            if (pendingLaunch is null)
            {
                return;
            }

            if (delaySeconds <= 0)
            {
                context.StartActivity(launchIntent);
                return;
            }

            if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarmManager)
            {
                context.StartActivity(launchIntent);
                return;
            }

            // ElapsedRealtime, а не время суток: отсчёт идёт от самой загрузки, и часы,
            // которые ещё могут подтянуться из сети, на него не влияют.
            alarmManager.Set(
                AlarmType.ElapsedRealtimeWakeup,
                SystemClock.ElapsedRealtime() + (delaySeconds * 1000L),
                pendingLaunch);
        }
    }
}
