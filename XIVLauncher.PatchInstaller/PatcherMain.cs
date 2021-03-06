﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.PatchInstaller.PatcherIpcMessages;
using XIVLauncher.PatchInstaller.ZiPatch;
using XIVLauncher.PatchInstaller.ZiPatch.Util;
using ZetaIpc.Runtime.Client;
using ZetaIpc.Runtime.Server;

namespace XIVLauncher.PatchInstaller
{
    public class PatcherMain
    {
        public const string BASE_GAME_VERSION = "2012.01.01.0000.0000";

        private static IpcServer _server = new IpcServer();
        private static IpcClient _client = new IpcClient();
        public const int IPC_SERVER_PORT = 0xff16;
        public const int IPC_CLIENT_PORT = 0xff30;

        public static JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full,
            TypeNameHandling = TypeNameHandling.All
        };

        static void Main(string[] args)
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console()
                    .WriteTo.File(Path.Combine(Paths.RoamingPath, "patcher.log"))
                    .WriteTo.Debug()
                    .MinimumLevel.Verbose()
                    .CreateLogger();

                if (args.Length > 1)
                {
                    try
                    {
                        InstallPatch(args[0], args[1]);
                        Log.Information("OK");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Patch installation failed.");
                        Environment.Exit(-1);
                    }

                    Environment.Exit(0);
                    return;
                }

                _client.Initialize(IPC_SERVER_PORT);
                _server.Start(IPC_CLIENT_PORT);
                _server.ReceivedRequest += ServerOnReceivedRequest;

                Log.Information("[PATCHER] IPC connected");

                SendIpcMessage(new PatcherIpcEnvelope
                {
                    OpCode = PatcherIpcOpCode.Hello,
                    Data = DateTime.Now
                });

                Log.Information("[PATCHER] sent hello");

                while (true)
                {
                    if (Process.GetProcesses().All(x => x.ProcessName != "XIVLauncher"))
                    {
                        Environment.Exit(0);
                        return;
                    }

                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Patcher init failed.\n\n" + ex, "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ServerOnReceivedRequest(object sender, ReceivedRequestEventArgs e)
        {
            Log.Information("[PATCHER] IPC: " + e.Request);

            var msg = JsonConvert.DeserializeObject<PatcherIpcEnvelope>(Base64Decode(e.Request), JsonSettings);

            switch (msg.OpCode)
            {
                case PatcherIpcOpCode.Bye:
                    Task.Run(() =>
                    {
                        Thread.Sleep(3000);
                        Environment.Exit(0);
                    });
                    break;  

                case PatcherIpcOpCode.StartInstall:
                    var installData = (PatcherIpcStartInstall) msg.Data;

                    // Ensure that subdirs exist
                    if (!installData.GameDirectory.Exists)
                        installData.GameDirectory.Create();

                    installData.GameDirectory.CreateSubdirectory("game");
                    installData.GameDirectory.CreateSubdirectory("boot");

                    Task.Run(() =>
                        InstallPatch(installData.PatchFile.FullName,
                            Path.Combine(installData.GameDirectory.FullName, installData.Repo == Repository.Boot ? "boot" : "game")))
                        .ContinueWith(t =>
                    {
                        if (!t.Result)
                        {
                            Log.Error(t.Exception, "PATCH INSTALL FAILED");
                            SendIpcMessage(new PatcherIpcEnvelope
                            {
                                OpCode = PatcherIpcOpCode.InstallFailed
                            });
                        }
                        else
                        {
                            try
                            {
                                installData.Repo.SetVer(installData.GameDirectory, installData.VersionId);
                                SendIpcMessage(new PatcherIpcEnvelope
                                {
                                    OpCode = PatcherIpcOpCode.InstallOk
                                });

                                try
                                {
                                    if (!installData.KeepPatch)
                                        installData.PatchFile.Delete();
                                }
                                catch (Exception exception)
                                {
                                    Log.Error(exception, "Could not delete patch file.");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Could not set ver file");
                                SendIpcMessage(new PatcherIpcEnvelope
                                {
                                    OpCode = PatcherIpcOpCode.InstallFailed
                                });
                            }
                        }
                    });
                    break;

                case PatcherIpcOpCode.Finish:
                    var path = (DirectoryInfo) msg.Data;
                    VerToBck(path);
                    Log.Information("VerToBck done");
                    break;
            }
        }

        private static void SendIpcMessage(PatcherIpcMessages.PatcherIpcEnvelope envelope)
        {
            _client.Send(Base64Encode(JsonConvert.SerializeObject(envelope, Formatting.None, JsonSettings)));
        }

        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        private static bool InstallPatch(string patchPath, string gamePath)
        {
            try
            {
                Log.Debug("Installing {0} to {1}", patchPath, gamePath);

                using var patchFile = ZiPatchFile.FromFileName(patchPath);

                using (var store = new SqexFileStreamStore())
                {
                    var config = new ZiPatchConfig(gamePath) { Store = store };

                    foreach (var chunk in patchFile.GetChunks())
                        chunk.ApplyChunk(config);
                }

                Log.Debug("Patch {0} installed", patchPath);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Patch install failed.");
                return false;
            }
        }

        private static void VerToBck(DirectoryInfo gamePath)
        {
            Thread.Sleep(200);

            foreach (var repository in Enum.GetValues(typeof(Repository)).Cast<Repository>())
            {
                // Overwrite the old BCK with the new game version
                try
                {
                    repository.GetVerFile(gamePath).CopyTo(repository.GetVerFile(gamePath, true).FullName, true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not copy to BCK");
                }
            }
        }
    }
}
