using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using ACT.UltraScouter.Common;
using ACT.UltraScouter.Config;
using ACT.UltraScouter.Config.UI.Views;
using ACT.UltraScouter.Models;
using ACT.UltraScouter.Models.FFLogs;
using ACT.UltraScouter.Workers;
using Advanced_Combat_Tracker;
using FFXIV.Framework.Bridge;
using FFXIV.Framework.Common;
using FFXIV.Framework.Extensions;
using FFXIV.Framework.FFXIVHelper;
using FFXIV.Framework.WPF.Views;
using NLog;

namespace ACT.UltraScouter
{
    public class PluginCore :
        IDisposable
    {
        #region Singleton

        private static PluginCore instance;

        public static PluginCore Instance => instance;

        public PluginCore()
        {
            instance = this;
        }

        public void Dispose()
        {
            instance = null;
        }

        #endregion Singleton

        #region Logger

        private Logger Logger => AppLog.DefaultLogger;

        #endregion Logger

        /// <summary>このプラグインの配置ディレクトリ</summary>
        public string PluginDirectory { get; set; }

        /// <summary>このプラグインの場所</summary>
        public string PluginLocation { get; set; }

        /// <summary>プラグインステータス表示用ラベル</summary>
        public Label PluginStatusLabel { get; private set; }

        /// <summary>プラグイン用タブページ表示用ラベル</summary>
        public TabPage PluginTabPage { get; private set; }

        public void EndPlugin()
        {
            try
            {
                EnvironmentHelper.GarbageLogs();
                this.Logger.Trace("start DeInitPlugin");

                // ターゲット情報ワーカを終了する
                MainWorker.Instance.End();

                // FFXIVプラグインへのアクセスを終了する
                FFXIVPlugin.Instance.End();
                FFXIVPlugin.Free();
                FFXIVReader.Free();

                // 設定ファイルを保存する
                Settings.Instance.Save();

                // 参照を開放する
                WavePlayer.Free();
                MainWorker.Free();
                Settings.Free();

                this.PluginStatusLabel.Text = "Plugin exited.";
                this.Logger.Trace("end DeInitPlugin. succeeded.");
            }
            catch (Exception ex)
            {
                this.Logger.Fatal(ex, "DeInitPlugin error.");
                this.ShowMessage("DeInitPlugin error.", ex);
            }
            finally
            {
                AppLog.FlushAll();
            }
        }

        public void StartPlugin(
            TabPage pluginScreenSpace,
            Label pluginStatusText)
        {
            // タイトルをセットする
            pluginScreenSpace.Text = "ULTRA SCOUTER";

            EnvironmentMigrater.Migrate();
            MasterFilePublisher.Publish();
            WPFHelper.Start();
            WPFHelper.BeginInvoke(async () =>
            {
                // FFXIV_MemoryReaderを先にロードさせる
                var result = await FFXIVReader.Instance.WaitForReaderToStartedAsync(pluginScreenSpace);

                AppLog.LoadConfiguration(AppLog.HojoringConfig);
                this.Logger.Trace(Assembly.GetExecutingAssembly().GetName().ToString() + " start.");
                result.WriteLog(this.Logger);

                try
                {
                    this.Logger.Trace("[ULTRA SCOUTER] Start InitPlugin");

                    this.PluginTabPage = pluginScreenSpace;
                    this.PluginStatusLabel = pluginStatusText;

                    // .NET FrameworkとOSのバージョンを確認する
                    if (!UpdateChecker.IsAvailableDotNet() ||
                        !UpdateChecker.IsAvailableWindows())
                    {
                        NotSupportedView.AddAndShow(pluginScreenSpace);
                        return;
                    }

                    // 設定ファイルを読み込む
                    Settings.Instance.Load();

                    // HojoringのSplashを表示する
                    WPFHelper.Start();
                    UpdateChecker.ShowSplash();

                    // 各種ファイルを読み込む
                    await Task.Run(() =>
                    {
                        TTSDictionary.Instance.Load();
                        Settings.Instance.MobList.LoadTargetMobList();
                    });

                    // FFXIVプラグインへのアクセスを開始する
                    await Task.Run(() =>
                    {
                        FFXIVPlugin.Instance.Start(
                            Settings.Instance.PollingRate,
                            Settings.Instance.FFXIVLocale);
                    });

                    // ターゲット情報ワーカを開始する
                    MainWorker.Instance.Start();

                    // タブページを登録する
                    this.SetupPluginTabPages(pluginScreenSpace);

                    // テキストコマンドの購読を追加する
                    this.SubscribeTextCommands();

                    this.PluginStatusLabel.Text = "Plugin started.";
                    this.Logger.Trace("[ULTRA SCOUTER] End InitPlugin");

                    // アップデートを確認する
                    await Task.Run(() => this.Update());

                    // FFLogsの統計データベースをロードする
                    StatisticsDatabase.Instance.Logger = Logger;
                    await StatisticsDatabase.Instance.LoadAsync();
                }
                catch (Exception ex)
                {
                    this.Logger.Fatal(ex, "InitPlugin error.");
                    this.ShowMessage("InitPlugin error.", ex);
                }
            });
        }

        /// <summary>
        /// プラグインのタブページを設定する
        /// </summary>
        /// <param name="baseTabPage">
        /// 基本のタブページ</param>
        private void SetupPluginTabPages(
            TabPage baseTabPage)
        {
            baseTabPage.Controls.Clear();
            baseTabPage.Controls.Add(new ElementHost()
            {
                Child = new BaseView(),
                Dock = DockStyle.Fill,
            });
        }

        /// <summary>
        /// メッセージの表示
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="ex">Exception</param>
        private void ShowMessage(
            string message,
            Exception ex = null)
        {
            var caption = "ULTRA SCOUTER";

            if (ex != null)
            {
                message += Environment.NewLine;
                message += Environment.NewLine;
                message += ex.ToString();
            }

            MessageBox.Show(
                ActGlobals.oFormActMain,
                message,
                caption,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        /// <summary>
        /// アップデートを行う
        /// </summary>
        private void Update()
        {
            if ((DateTime.Now - Settings.Instance.LastUpdateDateTime).TotalHours
                >= Settings.UpdateCheckInterval)
            {
                var message = UpdateChecker.Update(
                    "ACT.UltraScouter",
                    Assembly.GetExecutingAssembly());
                if (!string.IsNullOrWhiteSpace(message))
                {
                    this.Logger.Fatal(message);
                }

                Settings.Instance.LastUpdateDateTime = DateTime.Now;
                WPFHelper.Invoke(() => Settings.Instance.Save());
            }
        }

        #region TextCommands

        private const string ParseCommand = "/parse";

        private static readonly Regex ParseCommandRegex = new Regex(
            $@"{ParseCommand}(\s+""(?<characterName>.+)"")?(\s+(?<serverName>.+))?",
            RegexOptions.Compiled |
            RegexOptions.IgnoreCase);

        private void SubscribeTextCommands()
        {
            // /parse コマンド
            TextCommandBridge.Instance.Subscribe(new TextCommand(
            (string logLine, out Match match) =>
            {
                match = null;

                if (!logLine.ContainsIgnoreCase(ParseCommand))
                {
                    return false;
                }

                match = ParseCommandRegex.Match(logLine);
                return match.Success;
            },
            (string logLine, Match match) =>
            {
                if (match == null ||
                    !match.Success)
                {
                    return;
                }

                var charName = match.Groups["characterName"].ToString();
                var serverName = match.Groups["serverName"].ToString();

                TargetInfoModel.GetFFLogsInfoFromTextCommand(
                    charName,
                    serverName);
            }));
        }

        #endregion TextCommands
    }
}
