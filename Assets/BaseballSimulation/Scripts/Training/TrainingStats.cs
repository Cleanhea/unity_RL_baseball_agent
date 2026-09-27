using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// TensorBoard에 올리는 야구 지표(docs/training-curriculum.md "TensorBoard 지표"). <see cref="TrainingEnvController"/>가
    /// 투구·타석·플레이가 끝날 때 호출한다. 값은 ML-Agents StatsRecorder로 보내고 mlagents-learn이 Behavior의 summary_freq마다
    /// 평균(히스토그램 지표는 분포도 함께)을 기록한다. 0/1 지표는 평균이 곧 비율이다.
    /// 학습기에 연결되지 않았으면 보내지 않고 키별 마지막 값만 남긴다(검증·디버그용). 보상·관측에는 영향이 없다.
    /// 고정 상대 평가 타석은 일반 지표에 넣지 않고 <see cref="BenchmarkBatterGroup"/>·<see cref="BenchmarkPitcherGroup"/> 묶음에 따로 기록한다.
    /// </summary>
    public sealed class TrainingStats
    {
        /// <summary>학습 중인 타자 대 기준 스크립트 투수(<see cref="BenchmarkPitcher"/>) 타석의 지표 묶음.</summary>
        public const string BenchmarkBatterGroup = "Benchmark Batter";
        /// <summary>학습 중인 투수 대 고정 타자 모델 타석의 지표 묶음.</summary>
        public const string BenchmarkPitcherGroup = "Benchmark Pitcher";
        /// <summary>Statcast 강한 타구 기준 95 mph(km/h).</summary>
        public const float HardHitKmh = 152.9f;

        private static readonly (PitchCall call, string key)[] PitchCalls =
        {
            (PitchCall.Ball, "Pitch Call/Ball"), (PitchCall.CalledStrike, "Pitch Call/Called Strike"),
            (PitchCall.SwingingStrike, "Pitch Call/Swinging Strike"), (PitchCall.Foul, "Pitch Call/Foul"),
            (PitchCall.InPlay, "Pitch Call/In Play"),
        };
        private static readonly (PitchType type, string key)[] PitchTypes =
        {
            (PitchType.FourSeam, "Pitch Type/Four Seam"), (PitchType.TwoSeam, "Pitch Type/Two Seam"),
            (PitchType.Curve, "Pitch Type/Curve"), (PitchType.Slider, "Pitch Type/Slider"),
            (PitchType.Changeup, "Pitch Type/Changeup"),
        };
        private static readonly (PitchEndReason reason, string key)[] PlayEnds =
        {
            (PitchEndReason.FlyOut, "Play End/Fly Out"), (PitchEndReason.ForceOut, "Play End/Force Out"),
            (PitchEndReason.TagOut, "Play End/Tag Out"), (PitchEndReason.RunnerSafe, "Play End/Runner Safe"),
            (PitchEndReason.RunScored, "Play End/Run Scored"), (PitchEndReason.HomeRun, "Play End/Home Run"),
            (PitchEndReason.GroundRuleDouble, "Play End/Ground Rule Double"), (PitchEndReason.Timeout, "Play End/Timeout"),
        };

        private readonly Dictionary<string, float> last = new Dictionary<string, float>();
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private int batterPitches;

        /// <summary>지금까지 기록한 값의 수(연결 여부와 무관).</summary>
        public int RecordCount { get; private set; }

        /// <summary>키의 마지막 기록 값. 한 번도 기록하지 않았으면 false다.</summary>
        public bool TryGetLast(string key, out float value) => last.TryGetValue(key, out value);

        /// <summary>키에 기록한 값의 수(검증용).</summary>
        public int CountOf(string key) => counts.TryGetValue(key, out int count) ? count : 0;

        /// <summary>
        /// 끝난 투구 하나. 판정, 스윙·존(타격된 공은 목표 위치로 존 여부를 근사), 구속·구종, 타구, 스윙 진단, 타자 투구 보상 성분을 기록한다.
        /// </summary>
        public void RecordPitch(PlayDirector director, BatterAgent batter, bool pitcherAgent)
        {
            PitchCallSnapshot call = director.GetPitchCall();
            if (call.Call != PitchCall.None)
            {
                foreach (var (value, key) in PitchCalls) Add(key, call.Call == value ? 1f : 0f);
                bool contact = director.HasContact;
                bool inZone = InZone(director, call);
                bool swing = call.SwingOffered;
                Add("Plate Discipline/Zone Rate", inZone ? 1f : 0f);
                Add("Plate Discipline/Swing Rate", swing ? 1f : 0f);
                Add(inZone ? "Plate Discipline/Zone Swing Rate" : "Plate Discipline/Chase Rate", swing ? 1f : 0f);
                if (swing) Add("Plate Discipline/Contact Rate", contact ? 1f : 0f);
            }

            if (director.TryGetLastPitch(out PitchCommand pitch))
            {
                Add("Pitch/Speed (km/h)", pitch.Speed * 3.6f);
                if (pitcherAgent)
                    foreach (var (type, key) in PitchTypes) Add(key, pitch.Type == type ? 1f : 0f);
            }

            BattedBallSnapshot hit = director.GetBattedBallSnapshot();
            if (director.HasContact && hit.ExitSpeed > 0f)
            {
                Add("Batted Ball/Exit Speed (km/h)", hit.ExitSpeed * 3.6f, StatAggregationMethod.Histogram);
                Add("Batted Ball/Launch Angle (deg)", hit.LaunchAngleDegrees, StatAggregationMethod.Histogram);
                Add("Batted Ball/Spray Angle (deg)", hit.SprayAngleDegrees, StatAggregationMethod.Histogram);
                // 수비가 공중에서 잡으면 페어/파울 판정 없이 인플레이가 된다. 판정이 난 타구만 비율에 넣는다.
                if (hit.Call != BattedBallCall.None && hit.Call != BattedBallCall.OutOfPlay)
                {
                    bool fair = hit.Call != BattedBallCall.Foul;
                    Add("Batted Ball/Fair", fair ? 1f : 0f);
                    Add("Batted Ball/Home Run", hit.Call == BattedBallCall.HomeRun ? 1f : 0f);
                    if (fair && hit.HasFirstTouch) Add("Batted Ball/Distance (m)", hit.Distance);
                }
            }

            BattingEvaluation swingEvaluation = director.GetBattingEvaluation();
            if (swingEvaluation.HasSwung)
            {
                if (swingEvaluation.HasTimingReference)
                    Add("Swing/Timing Error (ms)", swingEvaluation.TimingErrorSeconds * 1000f, StatAggregationMethod.Histogram);
                if (swingEvaluation.HasClosestDistance)
                    Add("Swing/Bat-Ball Distance (cm)", swingEvaluation.ClosestDistance * 100f);
            }

            // 이번 투구의 보상이 확정됐을 때만(이전 투구 값을 다시 쓰지 않게) 성분을 기록한다.
            if (batter.CompletedPitches != batterPitches)
            {
                batterPitches = batter.CompletedPitches;
                BatterRewardSnapshot reward = batter.LastPitchReward;
                Add("Batter Reward/Contact", reward.Contact);
                Add("Batter Reward/Miss", reward.Miss);
                Add("Batter Reward/Exit Speed", reward.ExitSpeed);
                Add("Batter Reward/Pop Fly", reward.PopFly);
                Add("Batter Reward/Foul", reward.Foul);
                Add("Batter Reward/Home Run", reward.HomeRun);
                Add("Batter Reward/Pitch Total", reward.Total);
            }
        }

        /// <summary>
        /// 끝난 타석 하나. 3단계(<paramref name="fielding"/>)에서 판정까지 끝난 인플레이는 출루와 플레이 결과도 기록한다.
        /// 1·2단계 인플레이는 수비가 없어 출루 여부를 모르므로 출루율에 넣지 않는다.
        /// </summary>
        public void RecordPlateAppearance(PlateAppearanceResult result, int pitches, float outcomeReward,
            bool fielding, bool resolved, PlaySummary play, float liveSeconds)
        {
            Add("Plate Appearance/Strikeout", result == PlateAppearanceResult.Strikeout ? 1f : 0f);
            Add("Plate Appearance/Walk", result == PlateAppearanceResult.Walk ? 1f : 0f);
            Add("Plate Appearance/In Play", result == PlateAppearanceResult.InPlay ? 1f : 0f);
            Add("Plate Appearance/Pitches", pitches);
            Add("Batter Reward/Outcome", outcomeReward);
            if (!fielding) return;

            bool inPlay = result == PlateAppearanceResult.InPlay;
            if (!inPlay || (resolved && play.Resolved))
                Add("Plate Appearance/On Base", result == PlateAppearanceResult.Walk || (inPlay && play.BatterBases > 0) ? 1f : 0f);
            if (!inPlay || !resolved || !play.Resolved) return;

            Add("Play/Outs", play.Outs);
            Add("Play/Runs", play.Runs);
            Add("Play/Batter Bases", play.BatterBases);
            Add("Play/Runner Outs", play.RunnerOuts);
            Add("Play/Bases Advanced", play.BasesAdvanced);
            Add("Play/Live Time (s)", liveSeconds);
            foreach (var (reason, key) in PlayEnds) Add(key, play.EndReason == reason ? 1f : 0f);
        }

        /// <summary>
        /// Agent끼리 대결한 타석의 승패. 타자 결과 보상(<see cref="PlayOutcomeRewards.BatterPlateAppearance"/>)의 부호로 정한다.
        /// 투구 단위 보상이 섞이지 않아 ML-Agents Self-play/ELO(마지막 단계 보상의 부호)와 달리 타석 결과만 나타낸다.
        /// </summary>
        public void RecordMatchup(float batterOutcomeReward)
        {
            Add("Matchup/Batter Win", batterOutcomeReward > 0f ? 1f : 0f);
            Add("Matchup/Pitcher Win", batterOutcomeReward < 0f ? 1f : 0f);
            Add("Matchup/Draw", batterOutcomeReward == 0f ? 1f : 0f);
        }

        /// <summary>고정 상대 평가 타석의 끝난 투구 하나를 <paramref name="group"/> 묶음에 기록한다. 존·스윙·컨택과 타구 질만 남긴다.</summary>
        public void RecordBenchmarkPitch(string group, PlayDirector director)
        {
            PitchCallSnapshot call = director.GetPitchCall();
            bool contact = director.HasContact;
            if (call.Call != PitchCall.None)
            {
                bool inZone = InZone(director, call);
                bool swing = call.SwingOffered;
                Add(group + "/Zone Rate", inZone ? 1f : 0f);
                Add(group + (inZone ? "/Zone Swing Rate" : "/Chase Rate"), swing ? 1f : 0f);
                if (swing) Add(group + "/Contact Rate", contact ? 1f : 0f);
            }
            BattedBallSnapshot hit = director.GetBattedBallSnapshot();
            if (!contact || hit.ExitSpeed <= 0f) return;
            float kmh = hit.ExitSpeed * 3.6f;
            Add(group + "/Exit Speed (km/h)", kmh);
            Add(group + "/Hard Hit", kmh >= HardHitKmh ? 1f : 0f);
            if (hit.Call != BattedBallCall.None && hit.Call != BattedBallCall.OutOfPlay)
                Add(group + "/Home Run", hit.Call == BattedBallCall.HomeRun ? 1f : 0f);
        }

        /// <summary>고정 상대 평가 타석 하나의 결과를 <paramref name="group"/> 묶음에 기록한다. 출루는 3단계 일반 지표와 같은 조건이다.</summary>
        public void RecordBenchmarkPlateAppearance(string group, PlateAppearanceResult result, float batterOutcomeReward,
            bool fielding, bool resolved, PlaySummary play)
        {
            Add(group + "/Strikeout", result == PlateAppearanceResult.Strikeout ? 1f : 0f);
            Add(group + "/Walk", result == PlateAppearanceResult.Walk ? 1f : 0f);
            Add(group + "/In Play", result == PlateAppearanceResult.InPlay ? 1f : 0f);
            Add(group + "/Batter Outcome", batterOutcomeReward);
            bool inPlay = result == PlateAppearanceResult.InPlay;
            if (fielding && (!inPlay || (resolved && play.Resolved)))
                Add(group + "/On Base", result == PlateAppearanceResult.Walk || (inPlay && play.BatterBases > 0) ? 1f : 0f);
        }

        /// <summary>3아웃으로 끝난 반 이닝의 득점. 무작위 상황으로 시작한 반 이닝은 상황을 정한 뒤의 득점이다.</summary>
        public void RecordHalfInning(int runs) => Add("Half Inning/Runs", runs);

        /// <summary>컨트롤러가 초기화한 투구 하나. 중단(1)의 평균이 중단 비율이다.</summary>
        public void RecordPlayEnd(bool aborted) => Add("Env/Aborted Play", aborted ? 1f : 0f);

        /// <summary>존 통과 여부. 타격된 공은 플레이트를 지나지 않으므로 투구 목표 위치로 근사한다.</summary>
        private static bool InZone(PlayDirector director, PitchCallSnapshot call) => director.HasContact
            ? StrikeZone.Intersects(new Vector3(call.AimLocation.x, StrikeZone.PlateDepth, call.AimLocation.y),
                director.EnvironmentConfig.BallRadius, call.ZoneBottom, call.ZoneTop)
            : call.InZone;

        private void Add(string key, float value, StatAggregationMethod method = StatAggregationMethod.Average)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            last[key] = value;
            counts[key] = CountOf(key) + 1;
            RecordCount++;
            // 학습기가 없으면 side channel 메시지가 쌓이기만 하므로 보내지 않는다.
            if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
                Academy.Instance.StatsRecorder.Add(key, value, method);
        }
    }
}
