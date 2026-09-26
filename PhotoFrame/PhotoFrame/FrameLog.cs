namespace PhotoFrame
{
    /// <summary>
    /// Короткие записи в системный журнал устройства.
    /// </summary>
    /// <remarks>
    /// System.Diagnostics.Debug в сборке Release вырезается компилятором, а рамка
    /// работает именно на Release: без этих записей о происходящем внутри можно судить
    /// только по экрану. Пишем немного и по делу — журнал читается через `adb logcat -s
    /// PhotoFrame`.
    /// </remarks>
    internal static class FrameLog
    {
        private const string Tag = "PhotoFrame";

        public static void Info(string message) => Android.Util.Log.Info(Tag, message);

        public static void Warn(string message) => Android.Util.Log.Warn(Tag, message);
    }
}
