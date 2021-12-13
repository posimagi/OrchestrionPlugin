﻿using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Logging;

namespace Orchestrion
{
    // TODO:
    // try to find what writes to bgm 0, block it if we are playing?
    //   or save/restore if we preempt it?
    // debug info of which priority is active
    //  notifications/logs of changes even to lower priorities?

    public class OrchestrionPlugin : IDalamudPlugin
    {
        public string Name => "Orchestrion";

        private const string SongListFile = "xiv_bgm.csv";
        private const string CommandName = "/porch";
        private const string NativeNowPlayingPrefix = "♪ ";

        private readonly DalamudPluginInterface pi;
        private readonly CommandManager commandManager;
        private readonly ChatGui chatGui;
        private readonly Framework framework;
        private readonly NativeUIUtil nui;
        private readonly Configuration configuration;
        private readonly SongList songList;
        private readonly BGMController bgmController;

        private readonly TextPayload nowPlayingPayload = new("Now playing ");
        private readonly TextPayload periodPayload = new(".");
        private readonly TextPayload emptyPayload = new("");
        private readonly TextPayload leftBracketPayload = new("[");
        private readonly TextPayload rightBracketPayload = new("]");
        
        private bool isPlayingReplacement = false;
        
        public int CurrentSong => bgmController.PlayingSongId == 0 ? bgmController.CurrentSongId : bgmController.PlayingSongId;
        public string LocalDir { get; private set; }

        public OrchestrionPlugin(
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] GameGui gameGui,
            [RequiredVersion("1.0")] ChatGui chatGui,
            [RequiredVersion("1.0")] CommandManager commandManager,
            [RequiredVersion("1.0")] Framework framework,
            [RequiredVersion("1.0")] SigScanner sigScanner
        )
        {
            pi = pluginInterface;
            this.commandManager = commandManager;
            this.chatGui = chatGui;
            this.framework = framework;

            configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            configuration.Initialize(pluginInterface, this);

            localDir = Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName);

            var songlistPath = Path.Combine(localDir, SongListFile);
            songList = new SongList(songlistPath, configuration, this);

            try
            {
                BGMAddressResolver.Setup(sigScanner);
                bgmController = new BGMController();
                bgmController.OnSongChanged += HandleSongChanged;
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "Failed to find BGM playback objects");
                bgmController = null;
            }

            nui = new NativeUIUtil(configuration, gameGui);

            commandManager.AddHandler(CommandName, new CommandInfo(OnDisplayCommand)
            {
                HelpMessage = "Displays the Orchestrion window, to view, change, or stop in-game BGM."
            });
            pluginInterface.UiBuilder.Draw += Display;
            pluginInterface.UiBuilder.OpenConfigUi += () => songList.SettingsVisible = true;
            framework.Update += OrchestrionUpdate;
        }

        private void OrchestrionUpdate(Framework unused)
        {
            bgmController.Update();

            if (configuration.ShowSongInNative)
                nui.Update();
        }

        public void Dispose()
        {
            framework.Update -= OrchestrionUpdate;
            songList.Dispose();
            nui.Dispose();
            pi.UiBuilder.Draw -= Display;
            commandManager.RemoveHandler(CommandName);
            pi.Dispose();
        }

        public void SetNativeDisplay(bool value)
        {
            // Somehow it was set to the same value. This should not occur
            if (value == configuration.ShowSongInNative) return;

            if (value)
            {
                nui.Init();
                var songName = songList.GetSongTitle(CurrentSong);
                nui.Update(NativeNowPlayingPrefix + songName);
            }
            else
                nui.Dispose();
        }

        private void OnDisplayCommand(string command, string args)
        {
            songList.Visible = !songList.Visible;
        }

        private void Display()
        {
            songList.Draw();
        }

        private void HandleSongChanged(int oldSongId, int oldPriority, int newSongId, int newPriority)
        {
            PluginLog.Debug($"Song ID changed from {oldSongId} to {newSongId}");

            // The user is playing a track manually, so keep playing
            if (bgmController.PlayingSongId != 0 && !isPlayingReplacement)
                return;

            // A replacement is available, so change to it
            if (configuration.SongReplacements.TryGetValue(newSongId, out var replacement))
            {
                PluginLog.Debug($"Song ID {newSongId} has a replacement of {replacement.ReplacementId}");
                
                // If the replacement is "do not change" and we are not playing something, play the previous track
                // If the replacement is "do not change" and we *are* playing something, it'll just keep playing
                // Else we're only here if we have a replacement, play that 
                if (replacement.ReplacementId == -1 && bgmController.PlayingSongId == 0)
                    PlaySong(bgmController.OldSongId, true);
                else if (replacement.ReplacementId != -1)
                    PlaySong(replacement.ReplacementId, true);
                return;
            }
            
            // A replacement is playing
            if (isPlayingReplacement)
            {
                isPlayingReplacement = false;
                StopSong();
            }
            
            SendSongEcho(newSongId);
            UpdateNui(newSongId);
        }
        
        public void PlaySong(int songId, bool isReplacement = false)
        {
            PluginLog.Debug($"Playing {songId}");
            isPlayingReplacement = isReplacement;
            bgmController.SetSong((ushort)songId, configuration.TargetPriority);
            SendSongEcho(songId, true);
            UpdateNui(songId, true);
        }

        public void StopSong()
        {
            PluginLog.Debug($"Stopping playing {bgmController.PlayingSongId}...");
            
            if (configuration.SongReplacements.TryGetValue(bgmController.CurrentSongId, out var replacement))
            {
                PluginLog.Debug($"Song ID {bgmController.CurrentSongId} has a replacement of {replacement.ReplacementId}...");
                
                if (replacement.ReplacementId == bgmController.PlayingSongId)
                {
                    // The replacement is the same track as we're currently playing
                    // There's no point in continuing to play, so fall through to stop
                    PluginLog.Debug($"But that's the song we're playing [{bgmController.PlayingSongId}], so let's stop");
                }
                else if (replacement.ReplacementId == -1)
                {
                    // We stopped playing a song and the song under it has a replacement, so play that
                    PlaySong(bgmController.OldSongId, true);
                    return;
                } 
                else
                {
                    // Otherwise, go back to the replacement ID (stop playing the song on TOP of the replacement)
                    PlaySong(replacement.ReplacementId, true);
                    return;    
                }
            }
            
            // If there was no replacement involved, we don't need to do anything else, just stop
            bgmController.SetSong(0, configuration.TargetPriority);
            SendSongEcho(bgmController.CurrentSongId);
            UpdateNui(bgmController.CurrentSongId);
        }
        
        private void UpdateNui(int songId, bool playedByOrch = false)
        {
            if (!configuration.ShowSongInNative) return;

            var songName = songList.GetSongTitle(songId);
            var suffix = "";
            if (configuration.ShowIdInNative)
            {
                if (!string.IsNullOrEmpty(songName))
                    suffix = " - ";
                suffix += $"{songId}";
            }

            var text = songName + suffix;

            text = playedByOrch ? $"{NativeNowPlayingPrefix} [{text}]" : $"{NativeNowPlayingPrefix} {text}";

            nui.Update(text);
        }

        private void SendSongEcho(int songId, bool playedByOrch = false)
        {
            var songName = songList.GetSongTitle(songId);
            if (!configuration.ShowSongInChat || string.IsNullOrEmpty(songName)) return;

            var payloads = new List<Payload>
            {
                nowPlayingPayload,
                playedByOrch ? leftBracketPayload : emptyPayload,
                EmphasisItalicPayload.ItalicsOn,
                new TextPayload(songName),
                EmphasisItalicPayload.ItalicsOff,
                playedByOrch ? rightBracketPayload : emptyPayload,
                periodPayload
            };

            chatGui.PrintChat(new XivChatEntry
            {
                Message = new SeString(payloads),
                Type = XivChatType.Echo
            });
        }
    }
}