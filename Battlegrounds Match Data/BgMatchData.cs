﻿using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

using HearthDb.Enums;

using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Utility;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Card = Hearthstone_Deck_Tracker.Hearthstone.Card;
using Core = Hearthstone_Deck_Tracker.Core;
using HearthDb;

namespace BattlegroundsMatchData
{

    public class TurnSnapshot
    {
        public string Minions = "";
        public string Hero;
        public int Turn;
        private DateTimeOffset dateTime;
        public void SetDateTime(DateTimeOffset d)
        {
            dateTime = d;
        }
        public string isSelf = "Yes";
        public string result = "Draw";
        public string GameID;
        public string player;

        public List<object> ToList(bool useDateTimeString)
        {
            return (List<object>)ToArgList().Select((key, index) =>
            {
                if (useDateTimeString && key.Item1 == "dateTime") return DateTimeToString();
                else return key.Item2;
            }).ToList();
        }

        public List<string> Headers()
        {
            return (List<string>)ToArgList().Select((key, val) => key.Item1).ToList();
        }

        public List<(string, object)> ToArgList()
        {
            return new List<(string, object)>
            {
                ("dateTime",   dateTime),
                ("hero", Hero),
                ("minions", Minions),
                ("turn", Turn),
                ("isSelf", isSelf),
                ("combatResult", result),
                ("gameId", GameID),
                ("player", player)
            };
        }

        public string DateTimeToString()
        {
            return dateTime.ToString("R");
        }

    }


    public class GameRecord
    {
        public List<int> TavernTierTimings = new List<int>();
        public int CurrentTavernTier = 1;
        public TurnSnapshot Snapshot = new TurnSnapshot();
        public List<TurnSnapshot> Histories = new List<TurnSnapshot>();
        public int Position;
        public string AvailableRaces = "";

        public void SetDateTime(DateTimeOffset d)
        {
            Snapshot.SetDateTime(d);
        }

        public string getDateTime()
        {
            return Snapshot.DateTimeToString();
        }

        public string player { get => Snapshot.player; set => Snapshot.player = value; }
        public int Rating;
        public int MmrChange;

        public List<string> Headers = new List<string> {
                "Date & Time","Hero","Position","MMR","Ending Minions", "Minion Types", "Turns taken to reach tavern tier 2","3","4","5","6", "Ending Turn", "Game ID", "Player"
            };

        public List<string> PaddedTavernTimings()
        {
            List<string> list = TavernTierTimings.ConvertAll(x => x.ToString());
            while (list.Count < 5)
            {
                list.Add("");
            }
            return list;
        }

        public List<object> ToList()
        {

            String dt = Snapshot.DateTimeToString();

            List<object> l = new List<object>
            {
                dt, Snapshot.Hero, Position, Rating, Snapshot.Minions, AvailableRaces
            };

            foreach (string turn in PaddedTavernTimings())
            {
                l.Add(turn);
            }

            l.Add(Snapshot.Turn);
            l.Add(Snapshot.GameID);
            l.Add(Snapshot.player);

            return l;
        }

    }

    public class BgMatchData
    {
        private static bool _checkRating = false;
        private static int _checkStats = 0;
        private static int _rating;
        private static GameRecord _record = new GameRecord(); // need to initialize here as well. GameStart isn't called if HDT is started midway through game
        private static Config _config;
        private static bool isInBattle = false;
        private static bool checkOppUpdate = false;
        private static int lastBattleTurn = 0;
        private static int lastRecordedTurn = 0;
        private static bool recordedTypes = false;

        public static BgMatchOverlay Overlay;

        private static Dictionary<GameTag, string> RelevantTags = new Dictionary<GameTag, string>()
        {
            [GameTag.TAUNT] = LocUtil.Get("GameTag_Taunt"),
            [GameTag.DIVINE_SHIELD] = LocUtil.Get("GameTag_DivineShield"),
            [GameTag.POISONOUS] = LocUtil.Get("GameTag_Poisonous"),
            [GameTag.WINDFURY] = LocUtil.Get("GameTag_Windfury"),
            [GameTag.DEATHRATTLE] = LocUtil.Get("GameTag_Deathrattle")
        };

        internal static bool InBgMode(string currentMethod)
        {
            if (Core.Game.CurrentGameMode != GameMode.Battlegrounds)
            {
                Log.Info($"{currentMethod} - Not in Battlegrounds Mode.");
                return false;
            }
            return true;
        }

        internal static string MinionToString(Entity entity)
        {
            if (entity == null) return null;

            string attack = entity.GetTag(GameTag.ATK).ToString();
            string health = entity.GetTag(GameTag.HEALTH).ToString();
            string info = $"{attack}/{health}";

            var tags = RelevantTags.Keys.Where(x => entity.HasTag(x)).Select(x => RelevantTags[x]);
            if (tags.Count() > 0)
            {
                info += " " + string.Join(", ", tags);
            }

            string str = $"{entity.LocalizedName} ({info})";

            return str;
        }

        internal static float MinionToAtk(Entity entity)
        {
            return entity.GetTag(GameTag.ATK);
        }

        internal static float MinionToHealth(Entity entity)
        {
            return entity.GetTag(GameTag.HEALTH);
        }


        internal static void PlayerPlay(Card card)
        {
            UpdateStats();
            _checkStats = 3; // check for 300ms after, since battlecries may have triggered
        }

        internal static void TurnStart(ActivePlayer player)
        {
            if (!InBgMode("Turn Start")) return;

            checkOppUpdate = false;

            int turn = Core.Game.GetTurnNumber();

            int level = Core.Game.PlayerEntity.GetTag(GameTag.PLAYER_TECH_LEVEL);

            while (_record.CurrentTavernTier != level)
            {
                _record.TavernTierTimings.Add(turn);
                _record.CurrentTavernTier++;
            }

            // take snapshot of current minions board state
            int playerId = Core.Game.Player.Id;

            TurnSnapshot Snapshot = CreatePlayerSnapshot(playerId, turn);


            Log.Info("Current minions in play: " + Snapshot.Minions);
            _record.Snapshot = Snapshot;

            if (_record.AvailableRaces == "") _record.AvailableRaces = getMinionTypes();

            UpdateStats();

            isInBattle = player == ActivePlayer.Opponent;

            if (isInBattle) lastBattleTurn = turn;

            if (turn >= _config.TurnToStartTrackingAllBoards)
            {
                if (isInBattle)
                {
                    _record.Histories.Add(Snapshot);

                    // record opponent's board too
                    TurnSnapshot OppSnapshot = CreatePlayerSnapshot(Core.Game.Opponent.Id, turn);
                    checkOppUpdate = true;
                    OppSnapshot.isSelf = "";
                    Log.Info($"Opponent: ({OppSnapshot.Hero}) - Minions in play: {OppSnapshot.Minions}");
                    _record.Histories.Add(OppSnapshot);
                }
                else if (_record.Histories.Count() >= 2)
                {
                    CsvConnector.WriteBoard(_record);
                    if (_config.SpreadsheetUploadEnabled) SpreadsheetConnector.WriteBoard(_record);
                    if (_config.GraphqlUploadEnabled) GraphqlConnector.WriteBoard(_record);

                    lastRecordedTurn = _record.Histories.Last().Turn;
                }
            }

        }

        private static void UpdateStats()
        {
            int playerId = Core.Game.Player.Id;

            var minions = Core.Game.Entities.Values
                    .Where(x => x.IsMinion && x.IsInPlay && x.IsControlledBy(playerId));

            if (minions.Count() == 0) return;

            float[] atk = minions
                    .Select(x => MinionToAtk(x))
                    .ToArray();

            float[] health = minions
                    .Select(x => MinionToHealth(x))
                    .ToArray();

            Overlay.UpdateTotalStats((int)atk.Sum(), (int)health.Sum());
            Overlay.UpdateAvgStats(atk.Average(), health.Average());
        }

        private static TurnSnapshot CreatePlayerSnapshot(int playerId, int turn)
        {
            TurnSnapshot Snapshot = new TurnSnapshot();
            var entities = Core.Game.Entities.Values
                    .Where(x => x.IsMinion && x.IsInPlay && x.IsControlledBy(playerId))
                    .Select(x => MinionToString(x))
                    .ToArray();

            Entity hero = Core.Game.Entities.Values
                .Where(x => x.IsHero && x.GetTag(GameTag.PLAYER_ID) == Core.Game.Player.Id)
                .FirstOrDefault();


            var heroCard = Database.GetCardFromId(hero.CardId);
            if (heroCard?.BattlegroundsSkinParentId > 0)
            {
                heroCard = Database.GetCardFromDbfId(heroCard.BattlegroundsSkinParentId, false);
            }
            else if (hero.GetTag(GameTag.BACON_PLAYER_RESULTS_HERO_OVERRIDE) > 0)
            {
                heroCard = Database.GetCardFromDbfId(hero.GetTag(GameTag.BACON_PLAYER_RESULTS_HERO_OVERRIDE), false);
            }

            Snapshot.Hero = heroCard.LocalizedName;
            if (entities.Length > 0)
            {
                Snapshot.Minions = entities.Aggregate((a, b) => a + ", " + b);
            }
            Snapshot.Turn = turn;
            Snapshot.SetDateTime(DateTimeOffset.Now);
            Snapshot.GameID = Core.Game.CurrentGameStats.GameId.ToString();
            Snapshot.player = Core.Game.Player.Name;

            return Snapshot;
        }

        // Sometimes, Bob's Tavern is wrongly recorded as the opponent hero. This happens when the opponent hasn't been updated before TurnStart is called.
        // So we need to check if the opponent has changed since we last took the snapshot.
        internal static void CheckOpponent(Card card)
        {

            if (!isInBattle || card == null || card.Type != "Hero") return;

            Log.Info($"Opponent hero in play: {card.LocalizedName}");

            if (!checkOppUpdate)
            {
                Log.Info($"No need to check if there is an updated opponent.");
                return;
            }

            Log.Info($"Recording opponent hero to history: {card.LocalizedName}");
            _record.Histories.Last().Hero = card.LocalizedName;

            checkOppUpdate = false;
        }

        internal static void GameStart()
        {
            if (!InBgMode("Game Start")) return;
            Log.Info("Starting game");
            _record = new GameRecord();

            Overlay.UpdateTotalStats(0, 0);
            Overlay.UpdateAvgStats(0, 0);

            lastBattleTurn = 0;
            lastRecordedTurn = 0;

        }

        public static void OnLoad(Config config)
        {
            _config = config;
            Log.Info($"Loaded Plugin. CSV Location: {config.CsvGameRecordLocation}");

        }

        internal static void GameEnd()
        {
            if (!InBgMode("Game End")) return;
            int playerId = Core.Game.Player.Id;
            Entity hero = Core.Game.Entities.Values
                .Where(x => x.IsHero && x.GetTag(GameTag.PLAYER_ID) == playerId)
                .First();
            _record.Position = hero.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE);

            Log.Info($"Game ended - {_record.Snapshot.Hero} - Position: {_record.Position}");

            CsvConnector.WriteBoard(_record);
            WriteGameRecord();
            if (lastBattleTurn != lastRecordedTurn && _record.Histories.Count >= 2)
            {
                if (_config.SpreadsheetUploadEnabled) SpreadsheetConnector.WriteBoard(_record);
                //if (_config.GraphqlUploadEnabled) GraphqlConnector.WriteBoard(_record);
            }

            //GameLogManager.SaveLog();
        }

        internal static void EntityDamage(PredamageInfo info)
        {
            if (!isInBattle || !info.Entity.IsHero) return;

            if (_record.Histories.Count() == 0) return;

            TurnSnapshot oppSnapshot = _record.Histories.Last();
            TurnSnapshot mySnapshot = _record.Histories[_record.Histories.Count - 2];

            // can't use info.Entity.IsCurrentPlayer because the "current player" tag is removed when it's a lethal hit

            if (info.Entity.IsControlledBy(Core.Game.Player.Id))
            {
                mySnapshot.result = "Lose";
                oppSnapshot.result = "Win";
            }
            else
            {
                mySnapshot.result = "Win";
                oppSnapshot.result = "Lose";
            }

            checkOppUpdate = false;

        }

        internal static void InMenu()
        {
            if (!InBgMode("In Menu")) return;

            _checkRating = true;
        }

        internal static void Update()
        {

            if (_checkStats > 0)
            {
                _checkStats--;
                UpdateStats();
            }
        }

        internal static string setToString(IEnumerable<string> set)
        {
            return string.Join(", ", set);
        }

        internal static string getMinionTypes()
        {
            HashSet<Race> availableRaces = BattlegroundsUtils.GetAvailableRaces(Core.Game.CurrentGameStats?.GameId);
            string races = setToString(availableRaces.Select(race => HearthDbConverter.RaceConverter(race)));

            Func<HearthDb.Card, GameTag, int> getTag = (HearthDb.Card card, GameTag tag) =>
            {
                return card.Entity.GetTag(tag);
            };


            HashSet<Race> Races = new HashSet<Race>();

            var baconCards = Cards.All.Values
                .Where(x => getTag(x, GameTag.TECH_LEVEL) > 0 && getTag(x, GameTag.IS_BACON_POOL_MINION) > 0);


            foreach (var race in baconCards.Select(x => x.Race))
                Races.Add(race);

            var unavailableRaces = setToString(Races.Where(x => !availableRaces.Contains(x) && x != Race.INVALID && x != Race.ALL).Select(x => HearthDbConverter.RaceConverter(x)));

            string minionTypes = $"Available: {races}; Unavailable: {unavailableRaces}";
            Log.Info($"Minion Types -- {minionTypes}");

            return minionTypes;
        }

        internal static void WriteGameRecord()
        {
            CsvConnector.WriteGameRecord(_record);
            //if (_config.GraphqlUploadEnabled) GraphqlConnector.WriteGameRecord(_record);
            //if (_config.SpreadsheetUploadEnabled) SpreadsheetConnector.WriteGameRecord(_record);

        }
    }

}
