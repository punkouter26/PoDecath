using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.Core;
using Unity.Services.Leaderboards;

namespace PoDecath.Sim
{
    /// <summary>
    /// Unity Gaming Services, used for two things: a copy of the season file in Cloud Save, so the cards
    /// and the standings follow the player to another device, and one leaderboard per event plus one for
    /// season totals, so the best performance ever seen on any device has somewhere to live.
    ///
    /// Everything here is optional and fails quietly. The game is complete without it: the season file
    /// on the device is the record, and this is a mirror. That matters because it needs things only the
    /// owner can do, all in the Unity Cloud dashboard:
    ///
    ///  1. Link the project (Edit > Project Settings > Services). Until then <see cref="Application.cloudProjectId"/>
    ///     is empty and nothing below is even attempted.
    ///  2. Turn on Authentication (anonymous sign-in), Cloud Save and Leaderboards for the project.
    ///  3. Create the leaderboards named by <see cref="BoardId"/>: <c>podecath_100m</c>,
    ///     <c>podecath_longjump</c>, <c>podecath_400m</c>, <c>podecath_hurdles</c>, <c>podecath_1500m</c> and
    ///     <c>podecath_season</c>, all sorted high-to-low (they hold points, not times).
    ///
    /// What gets posted is points, the winner's name travelling as metadata. The athletes are AIs, so
    /// "player" on these boards means "this device": its best race, and who ran it.
    /// </summary>
    public static class SeasonCloud
    {
        public enum State { Off, Connecting, Ready, Failed }

        public static State Status { get; private set; } = State.Off;
        /// <summary>One line for the menu: what the cloud is doing and, when it is not, why not.</summary>
        public static string StatusLine { get; private set; } = "Cloud: not linked";

        static Task<bool> _connecting;
        static bool _posted, _boardsMissing;

        public static bool Linked => !string.IsNullOrEmpty(Application.cloudProjectId);

        /// <summary>
        /// Call from Start or later, never from Awake/OnEnable of a scene's first objects. In the editor each
        /// UGS package clears its singleton in a <c>RuntimeInitializeOnLoadMethod</c> that runs after the
        /// first scene has loaded; initialising before that point completes, and then the service is wiped
        /// out from under it ("The Leaderboards service has not been initialized"). Found on 2026-09-29 when
        /// every post from the first test races failed that way.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _connecting = null;
            _posted = _boardsMissing = false;
            Status = State.Off;
            StatusLine = "Cloud: not linked";
        }

        public static string BoardId(SeasonEvent evt) => evt switch
        {
            SeasonEvent.Sprint100 => "podecath_100m",
            SeasonEvent.LongJump => "podecath_longjump",
            SeasonEvent.Run400 => "podecath_400m",
            SeasonEvent.Hurdles => "podecath_hurdles",
            SeasonEvent.Run1500 => "podecath_1500m",
            _ => "podecath_other",
        };

        public const string SeasonBoard = "podecath_season";
        const string SaveKey = "season";

        /// <summary>Initialises the services and signs in anonymously, once. True when the cloud is usable.</summary>
        public static Task<bool> Connect()
        {
            if (!Linked) { Status = State.Off; StatusLine = "Cloud: project not linked"; return Task.FromResult(false); }
            if (_connecting != null) return _connecting;
            _connecting = ConnectAsync();
            return _connecting;
        }

        static async Task<bool> ConnectAsync()
        {
            Status = State.Connecting;
            StatusLine = "Cloud: connecting";
            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                    await UnityServices.InitializeAsync();
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Status = State.Ready;
                StatusLine = _posted ? "Cloud: saved and ranked online" : "Cloud: saved online";
                return true;
            }
            catch (Exception e)
            {
                Status = State.Failed;
                StatusLine = "Cloud: offline";
                // Once, not per race: an unlinked or offline cloud is a state, not an event.
                Debug.Log($"[SeasonCloud] cloud unavailable, the season stays on this device only: {e.Message}");
                _connecting = null;
                return false;
            }
        }

        /// <summary>Pulls the cloud copy of the season file and adopts it if it is newer than this device's.</summary>
        public static async void Pull(Action<bool> done = null)
        {
            bool adopted = false;
            try
            {
                if (await Connect())
                {
                    var items = await CloudSaveService.Instance.Data.Player.LoadAsync(new HashSet<string> { SaveKey });
                    if (items != null && items.TryGetValue(SaveKey, out var item))
                        adopted = SeasonStore.Adopt(item.Value.GetAs<string>());
                }
            }
            catch (Exception e) { Debug.Log($"[SeasonCloud] pull skipped: {e.Message}"); }
            done?.Invoke(adopted);
        }

        /// <summary>Mirrors the season file to Cloud Save. Fire and forget; the device copy is already written.</summary>
        public static async void Push(string json)
        {
            try
            {
                if (!await Connect()) return;
                await CloudSaveService.Instance.Data.Player.SaveAsync(new Dictionary<string, object> { { SaveKey, json } });
            }
            catch (Exception e) { Debug.Log($"[SeasonCloud] save skipped: {e.Message}"); }
        }

        /// <summary>Posts one score, with who scored it. A board that does not exist yet is logged once and skipped.</summary>
        public static async void Submit(string boardId, int points, string athlete)
        {
            if (points <= 0) return;
            try
            {
                if (!await Connect()) return;
                await LeaderboardsService.Instance.AddPlayerScoreAsync(boardId, points,
                    new AddPlayerScoreOptions { Metadata = new Dictionary<string, string> { { "athlete", athlete } } });
                _posted = true;
                StatusLine = "Cloud: saved and ranked online";
            }
            catch (Exception e)
            {
                // Almost always a board that has not been created in the dashboard yet. Said once in the
                // status line, logged every time with the board's name so the owner knows which to create.
                if (!_boardsMissing) StatusLine = "Cloud: saved online, leaderboards not set up yet";
                _boardsMissing = true;
                Debug.Log($"[SeasonCloud] {boardId} not posted: {e.Message}");
            }
        }
    }
}
