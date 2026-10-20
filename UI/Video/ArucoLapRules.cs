using Composition.Input;
using Microsoft.Xna.Framework.Input;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timing;
using Timing.Aruco;
using Tools;

namespace UI.Video
{
    /// <summary>
    /// Lap rules for ArUco timing, applied before a crossing reaches RaceManager, plus the
    /// Alt + NumPad manual lap that gives way to ArUco.
    ///
    /// Lapalyser's automatic check is written for RF timers: of two detections closer than the
    /// Min Lap Time it cannot tell which is false, so it guesses from the neighbouring lap
    /// lengths. ArUco knows: a gate pass ends when the marker leaves view, so a marker that
    /// drops out on the way through reports an early crossing and then the real one. Deciding
    /// here, so that every lap ArUco hands over is at least the Min Lap Time long, means the
    /// automatic check never fires for ArUco laps and never second-guesses these rules.
    ///
    /// A crossing closer than the Min Lap Time to the previous lap:
    ///  - after a standard manual lap (Alt + number, lap editor, ...): dropped, the manual lap
    ///    is always kept;
    ///  - after an ArUco lap: dropped, or with Correct Flicker Laps on, the previous one is
    ///    disqualified and this one kept.
    /// A crossing that is kept disqualifies any Alt + NumPad lap within the Min Lap Time of it,
    /// before or after.
    ///
    /// Alt + NumPad 0-9 (listening frequencies in order, as the Dummy timer) records a manual lap
    /// unless an ArUco lap or another Alt + NumPad lap is less than the Min Lap Time before it.
    ///
    /// Only acts while an ArUco system is the Primary and a race is running. A dropped crossing
    /// is still in the marker-count log, so the marshal screen can bring it back.
    /// </summary>
    public class ArucoLapRules : IDisposable
    {
        private static readonly Keys[] numPadKeys = new Keys[]
        {
            Keys.NumPad0, Keys.NumPad1, Keys.NumPad2, Keys.NumPad3, Keys.NumPad4,
            Keys.NumPad5, Keys.NumPad6, Keys.NumPad7, Keys.NumPad8, Keys.NumPad9
        };

        private readonly EventManager eventManager;
        private readonly RaceManager raceManager;
        private readonly TimingSystemManager timingSystemManager;

        // Crossings arrive on the detection thread and key presses on the UI thread. Each decides
        // from the laps the other may be changing, so both run under this lock.
        private readonly object sync = new object();

        // Laps entered with Alt + NumPad. Both kinds of manual lap are TimingSystemType.Manual, so
        // this is the only way to tell the ones that give way to ArUco from the ones that never do.
        // By reference: BaseObject hashes on ID, which the database only assigns when the lap is
        // recorded, after the detection has gone into the set.
        private readonly HashSet<Detection> numPadDetections = new HashSet<Detection>(ReferenceEqualityComparer.Instance);

        public ArucoLapRules(EventManager eventManager)
        {
            this.eventManager = eventManager;
            raceManager = eventManager.RaceManager;
            timingSystemManager = raceManager.TimingSystemManager;

            // The timing systems are rebuilt on every settings change, so hook each new set.
            timingSystemManager.OnInitialise += Attach;
            raceManager.OnRaceStart += ClearNumPadLaps;
            raceManager.OnRaceReset += ClearNumPadLaps;
            Attach();
        }

        public void Dispose()
        {
            timingSystemManager.OnInitialise -= Attach;
            raceManager.OnRaceStart -= ClearNumPadLaps;
            raceManager.OnRaceReset -= ClearNumPadLaps;

            foreach (ArucoTimingSystem sys in ArucoSystems)
            {
                if (sys.CrossingHandler == OnCrossing)
                {
                    sys.CrossingHandler = null;
                }
            }
        }

        private IEnumerable<ArucoTimingSystem> ArucoSystems
        {
            get
            {
                return timingSystemManager.TimingSystems?.OfType<ArucoTimingSystem>() ?? Enumerable.Empty<ArucoTimingSystem>();
            }
        }

        private ArucoTimingSystem Primary
        {
            get
            {
                return timingSystemManager.PrimeSystems?.OfType<ArucoTimingSystem>().FirstOrDefault();
            }
        }

        private void Attach()
        {
            // Every instance gets it; ArucoTimingSystem only asks while it is the Primary.
            foreach (ArucoTimingSystem sys in ArucoSystems)
            {
                sys.CrossingHandler = OnCrossing;
            }
        }

        private void ClearNumPadLaps(Race race)
        {
            lock (sync)
            {
                numPadDetections.Clear();
            }
        }

        private bool IsNumPad(Lap lap)
        {
            // A lap an operator has since set by hand is theirs: it no longer gives way.
            return numPadDetections.Contains(lap.Detection) && lap.Detection.ValidityType == Detection.ValidityTypes.Auto;
        }

        private TimeSpan MinLapTime
        {
            get
            {
                return eventManager.Event?.MinLapTime ?? TimeSpan.Zero;
            }
        }

        private static Lap[] GetValidLaps(Race race, Pilot pilot)
        {
            return race.GetLaps(l => l.Pilot == pilot && l.Detection != null && l.Detection.Valid);
        }

        private void OnCrossing(int frequency, DateTime time, Action emit)
        {
            lock (sync)
            {
                Race race = raceManager.CurrentRace;
                Pilot pilot = race?.GetPilotChannel(frequency)?.Pilot;
                TimeSpan minLapTime = MinLapTime;

                if (race == null || !race.Running || !race.Type.HasLaps() || pilot == null || minLapTime <= TimeSpan.Zero)
                {
                    emit();
                    return;
                }

                // RaceManager invalidates a crossing before the start or inside the ignore window
                // itself; that stays exactly as it was.
                DateTime pilotStart = race.GetHandicappedStart(pilot);
                if (time < pilotStart + eventManager.Event.RaceStartIgnoreDetections)
                {
                    emit();
                    return;
                }

                Lap[] laps = GetValidLaps(race, pilot);
                Lap previous = laps
                    .Where(l => !IsNumPad(l) && l.Detection.Time < time)
                    .OrderByDescending(l => l.Detection.Time)
                    .FirstOrDefault();

                Lap replace = null;
                string drop = null;
                DateTime reference = pilotStart;

                if (previous != null)
                {
                    reference = previous.Detection.Time;
                    if (time - reference < minLapTime)
                    {
                        if (previous.Detection.TimingSystemType == TimingSystemType.Manual)
                        {
                            drop = "a manual lap";
                        }
                        else if (previous.Detection.ValidityType != Detection.ValidityTypes.Auto)
                        {
                            // Set by an operator or marshal: never undone from here.
                            drop = "a lap set by hand";
                        }
                        else if (Primary?.ArucoSettings?.CorrectFlickerLaps ?? false)
                        {
                            replace = previous;
                        }
                        else
                        {
                            drop = "the previous crossing";
                        }
                    }
                }
                else if (race.PrimaryTimingSystemLocation != PrimaryTimingSystemLocation.Holeshot && time - pilotStart < minLapTime)
                {
                    // Without a holeshot the first lap runs from the start.
                    drop = "the race start";
                }

                if (drop != null)
                {
                    Log("dropped " + Describe(pilot, time) + ", " + Seconds(time - reference) + " after " + drop);
                    return;
                }

                if (replace != null)
                {
                    raceManager.DisqualifyLap(replace, Detection.ValidityTypes.Auto);
                    Log("flicker lap: kept " + Describe(pilot, time) + ", dropped " + replace.Detection.Time.ToString("HH:mm:ss.fff")
                        + " (" + Seconds(time - replace.Detection.Time) + " apart)");
                }

                // Before or after: a NumPad lap pressed just after the crossing can already be in
                // if the crossing is still on its way, which takes FlickerLengthMs and processing.
                foreach (Lap manual in laps.Where(l => IsNumPad(l) && (l.Detection.Time - time).Duration() < minLapTime))
                {
                    raceManager.DisqualifyLap(manual, Detection.ValidityTypes.Auto);
                    Log("replaced NumPad lap " + manual.Detection.Time.ToString("HH:mm:ss.fff") + " with " + Describe(pilot, time));
                }

                emit();

                // A crossing that lands before a lap already recorded - a standard manual lap
                // pressed while it was on its way - leaves that lap measured and numbered from
                // the one before.
                if (GetValidLaps(race, pilot).Any(l => l.Detection.Time > time))
                {
                    raceManager.RecalcuateLaps(pilot, race);
                }
            }
        }

        /// <summary>
        /// Alt + NumPad 0-9. Returns true when the key was this one's, whether or not it recorded
        /// a lap.
        /// </summary>
        public bool OnKeyboardInput(KeyboardInputEvent inputEvent, bool onReplay)
        {
            if (!inputEvent.Alt || inputEvent.Ctrl || inputEvent.Shift)
                return false;

            int index = Array.IndexOf(numPadKeys, inputEvent.Key);
            if (index < 0)
                return false;

            // Replays are corrected with the standard Alt + number keys.
            if (onReplay || Primary == null)
                return false;

            try
            {
                lock (sync)
                {
                    AddNumPadLap(index, inputEvent.Creation);
                }
            }
            catch (Exception ex)
            {
                Logger.TimingLog?.LogException(this, ex);
            }
            return true;
        }

        private void AddNumPadLap(int index, DateTime time)
        {
            Race race = raceManager.CurrentRace;
            if (race == null || !race.Running || !race.Type.HasLaps())
                return;

            ListeningFrequency[] frequencies = timingSystemManager.LastListeningFrequencies;
            if (frequencies == null || index >= frequencies.Length)
                return;

            RacePilotChannel pilotChannel = race.GetPilotChannel(frequencies[index].Frequency);
            Pilot pilot = pilotChannel?.Pilot;
            Channel channel = pilotChannel?.Channel;
            if (pilot == null || channel == null)
                return;

            TimeSpan minLapTime = MinLapTime;
            if (minLapTime > TimeSpan.Zero)
            {
                Lap blocking = GetValidLaps(race, pilot)
                    .Where(l => IsNumPad(l) || l.Detection.TimingSystemType != TimingSystemType.Manual)
                    .Where(l => time >= l.Detection.Time && time - l.Detection.Time < minLapTime)
                    .FirstOrDefault();

                if (blocking != null)
                {
                    Log("ignored NumPad lap " + Describe(pilot, time) + ", " + Seconds(time - blocking.Detection.Time)
                        + " after " + (IsNumPad(blocking) ? "a NumPad lap" : "a crossing"));
                    return;
                }
            }

            // As RaceManager.AddManualLap, but keeping the detection so it can be recognised later.
            Detection detection = new Detection(TimingSystemType.Manual, 0, pilot, channel, time, raceManager.GetCurrentLapNumber(pilot) + 1, true, 0);
            numPadDetections.Add(detection);
            raceManager.AddLap(detection);
            raceManager.RecalcuateLaps(pilot, race);

            Log("NumPad lap " + Describe(pilot, time));
        }

        private static string Describe(Pilot pilot, DateTime time)
        {
            return time.ToString("HH:mm:ss.fff") + " (" + pilot.Name + ")";
        }

        private static string Seconds(TimeSpan span)
        {
            return span.TotalSeconds.ToString("0.000") + "s";
        }

        private void Log(string text)
        {
            Logger.TimingLog?.Log(this, "[ArUco] " + text);
        }
    }
}
