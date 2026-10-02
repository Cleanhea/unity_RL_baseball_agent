using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// 열린 학습 씬의 단계별 규칙을 일시정지한 Play Mode에서 확인한다(docs/verification.md).
    /// 모든 Agent를 중립 휴리스틱으로 두고 Academy → Director → 물리 순서로 직접 진행한다.
    /// 필요한 경우 같은 명령 경계(PlayDirector 요청)로 중립 행동을 덮어써 상황을 만든다.
    /// </summary>
    public static class TrainingStageVerification
    {
        private const int Plays = 12;
        // 3단계 시나리오 C·D·J의 안타(분사각·발사각·속력). 투수(정면 18.7 m)와 내야수 머리 위를 넘고 중견수 정면을 피한다.
        private const float SingleSprayDegrees = -8f;
        private const float SingleLaunchDegrees = 12f;
        private const float SingleSpeed = 34f;
        private const int SpreadSamples = 20000;
        /// <summary>격자 바깥 칸 투구 중 입체 존을 스쳐 스트라이크가 되어도 되는 최대 비율. 넘으면 바깥 칸이 볼이라는 설계가 깨진 것이다.</summary>
        private const float MaxOuterCellStrikeRate = 0.05f;

        [MenuItem("Tools/Baseball Simulation/Training/Verify Open Training Scene (Paused Play Mode)", false, 40)]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            TrainingEnvController controller = BatterAgentVerification.FindPrimaryController();
            if (controller == null) throw new InvalidOperationException("Open a training scene first.");
            controller.SetRandomSituationProbability(0f);
            var behaviors = new List<BehaviorParameters>();
            var previous = new List<BehaviorType>();
            SimulationMode previousPhysics = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                // 평가 타석(고정 타자 모델)이 진행 중이면 타자 정책이 바뀌어 있으므로, 끝낸 뒤에 원래 Behavior 종류를 저장한다.
                BatterAgentVerification.DisableBenchmarks(controller);
                foreach (Unity.MLAgents.Agent agent in UnityEngine.Object.FindObjectsByType<Unity.MLAgents.Agent>(FindObjectsInactive.Exclude))
                    behaviors.Add(agent.GetComponent<BehaviorParameters>());
                foreach (BehaviorParameters behavior in behaviors) previous.Add(behavior.BehaviorType);
                foreach (BehaviorParameters behavior in behaviors) behavior.BehaviorType = BehaviorType.HeuristicOnly;
                BatterAgentVerification.FinishCurrentPlateAppearance(controller);
                switch (controller.Stage)
                {
                    case TrainingStage.Batter: return RunStage1(controller);
                    case TrainingStage.BatterPitcher: return RunStage2(controller);
                    default: return RunStage3(controller);
                }
            }
            finally
            {
                for (int i = 0; i < behaviors.Count; i++) behaviors[i].BehaviorType = previous[i];
                Physics.simulationMode = previousPhysics;
            }
        }

        // ───────────────────────── 1단계 ─────────────────────────

        private static string RunStage1(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            BatterAgent batter = controller.Batter;
            BaseballEnvironmentConfig config = director.EnvironmentConfig;
            Vector2 spread = controller.ScriptedLocationSpread;
            Vector2 center = director.StrikeZoneCenter;
            Vector2 half = BenchmarkPitcher.LocationHalfRange(config);

            // 위치 분포 표본: 존 중앙 기준 정규 분포를 플레이트·존 ± 여유로 자른다. 기본 표준편차면 존 통과가 약 절반이다.
            var sampler = new System.Random(12345);
            int sampledInZone = 0;
            for (int i = 0; i < SpreadSamples; i++)
            {
                Vector2 aim = BenchmarkPitcher.SampleLocation(sampler, config, center, spread);
                Require(Mathf.Abs(aim.x) <= half.x + 1e-5f && Mathf.Abs(aim.y - center.y) <= half.y + 1e-5f, "sampled aim inside the location range");
                if (StrikeZone.Intersects(new Vector3(aim.x, StrikeZone.PlateDepth, aim.y), config.BallRadius, config.StrikeZoneBottom, config.StrikeZoneTop))
                    sampledInZone++;
            }
            float sampledZoneRate = sampledInZone / (float)SpreadSamples;
            if (spread == BenchmarkPitcher.LocationSpread)
                Require(sampledZoneRate > 0.42f && sampledZoneRate < 0.58f, $"default spread zone rate {sampledZoneRate:P1} between 42% and 58%");

            var pitches = new List<(PitchCommand pitch, PitchCallSnapshot call, float arrival)>();
            void OnCalled(PitchCall call)
            {
                if (director.TryGetLastPitch(out PitchCommand pitch))
                    pitches.Add((pitch, director.GetPitchCall(), director.PitchArrivalSeconds));
            }
            director.PitchCalled += OnCalled;
            try
            {
                int startPlays = controller.CompletedPlays, startAborted = controller.AbortedPlays, startEpisodes = batter.CompletedEpisodes;
                // 타석이 닫힐 때마다 그 타석의 판정 순서로 볼카운트를 다시 세어 결과와 맞춘다. 중립 타자는 스윙하지 않아 볼넷 또는 삼진이다.
                int plateAppearances = 0, walks = 0, strikeouts = 0, counted = 0;
                // 판정이 난 투구의 지표 기록(초기화)까지 진행해 마지막 투구·타석 지표가 위 판정과 짝이 맞게 한다.
                for (int i = 0; i < Plays * 400 && (pitches.Count < 2 * Plays || plateAppearances < Plays / 3 ||
                    controller.CompletedPlays - startPlays < pitches.Count); i++)
                {
                    BatterAgentVerification.Step(director);
                    if (batter.CompletedEpisodes - startEpisodes == plateAppearances) continue;
                    plateAppearances++;
                    int balls = 0, strikes = 0;
                    for (; counted < pitches.Count; counted++)
                    {
                        if (pitches[counted].call.Call == PitchCall.Ball) balls++;
                        else strikes++;
                    }
                    PlateAppearanceResult result = controller.LastPlateAppearanceResult;
                    Require(result == PlateAppearanceResult.Walk ? balls == 4 && strikes < 3
                        : result == PlateAppearanceResult.Strikeout && strikes == 3 && balls < 4,
                        $"plate appearance {plateAppearances}: {balls} balls and {strikes} strikes ended in {result}");
                    if (result == PlateAppearanceResult.Walk) walks++;
                    else strikeouts++;
                }
                Require(pitches.Count >= 2 * Plays && plateAppearances >= Plays / 3,
                    $"{2 * Plays} pitches and {Plays / 3} plate appearances played (got {pitches.Count}, {plateAppearances})");
                Require(controller.AbortedPlays == startAborted, "no aborted plays");
                Require(controller.CompletedPlays - startPlays == pitches.Count, "controller reset after each pitch");

                Vector2 range = controller.ScriptedSpeedRangeKmh;
                float radius = config.BallRadius, plateHalf = StrikeZone.PlateWidth * 0.5f;
                float minKmh = float.MaxValue, maxKmh = float.MinValue, maxMiss = 0f, maxOffset = 0f, minArrival = float.MaxValue, maxArrival = 0f;
                int calledStrikes = 0, calledBalls = 0;
                foreach (var (pitch, call, arrival) in pitches)
                {
                    float kmh = pitch.Speed * 3.6f;
                    minKmh = Mathf.Min(minKmh, kmh);
                    maxKmh = Mathf.Max(maxKmh, kmh);
                    minArrival = Mathf.Min(minArrival, arrival);
                    maxArrival = Mathf.Max(maxArrival, arrival);
                    Require(pitch.Type == PitchType.FourSeam, "stage 1 throws four-seam fastballs only");
                    Require(kmh >= range.x - 0.01f && kmh <= range.y + 0.01f, $"speed {kmh:F1} km/h inside {range}");
                    Vector2 aim = pitch.PlateLocation;
                    Require(Mathf.Abs(aim.x) <= half.x + 1e-5f && Mathf.Abs(aim.y - center.y) <= half.y + 1e-5f, "aim inside the location range");
                    maxOffset = Mathf.Max(maxOffset, Vector2.Distance(aim, center));
                    Require(call.HasPlateLocation, "plate crossing measured");
                    float miss = Vector2.Distance(call.PlateLocation, aim);
                    maxMiss = Mathf.Max(maxMiss, miss);
                    Require(miss < 0.03f, $"ball crossed within 3 cm of its aim (miss {miss:F3} m)");
                    Require(!call.SwingOffered, "neutral batter takes every pitch");
                    // 목표가 존 가장자리에서 3 cm 넘게 안쪽이면 스트라이크, 공 반지름 + 3 cm 넘게 바깥이면 볼이어야 한다.
                    bool clearlyIn = Mathf.Abs(aim.x) < plateHalf - 0.03f && aim.y > config.StrikeZoneBottom + 0.03f && aim.y < config.StrikeZoneTop - 0.03f;
                    bool clearlyOut = Mathf.Abs(aim.x) > plateHalf + radius + 0.03f ||
                        aim.y < config.StrikeZoneBottom - radius - 0.03f || aim.y > config.StrikeZoneTop + radius + 0.03f;
                    if (clearlyIn) Require(call.Call == PitchCall.CalledStrike, $"taken pitch well inside the zone at {aim} is a called strike (got {call.Call})");
                    if (clearlyOut) Require(call.Call == PitchCall.Ball, $"taken pitch well outside the zone at {aim} is a ball (got {call.Call})");
                    Require(call.Call == (call.InZone ? PitchCall.CalledStrike : PitchCall.Ball), "taken pitch call follows the zone test");
                    if (call.Call == PitchCall.CalledStrike) calledStrikes++;
                    else calledBalls++;
                }
                Require(maxKmh - minKmh > 5f, "speeds vary between pitches");
                if (spread.x > 0f || spread.y > 0f)
                    Require(calledStrikes > 0 && calledBalls > 0 && maxOffset > 0.1f, "locations vary: both called strikes and balls were thrown");
                else
                    Require(calledBalls == 0 && maxOffset < 0.001f, "zero spread throws every pitch at the zone center");

                // TensorBoard 지표: 마지막 투구·타석 값.
                PitchCall lastCall = pitches[pitches.Count - 1].call.Call;
                RequireStat(controller, "Pitch Call/Ball", lastCall == PitchCall.Ball ? 1f : 0f);
                RequireStat(controller, "Plate Discipline/Zone Rate", lastCall == PitchCall.CalledStrike ? 1f : 0f);
                RequireStat(controller, "Plate Discipline/Swing Rate", 0f);
                bool lastWalk = controller.LastPlateAppearanceResult == PlateAppearanceResult.Walk;
                RequireStat(controller, "Plate Appearance/Walk", lastWalk ? 1f : 0f);
                RequireStat(controller, "Plate Appearance/Strikeout", lastWalk ? 0f : 1f);
                RequireStat(controller, "Batter Reward/Outcome", lastWalk ? PlayOutcomeRewards.WalkValue : -PlayOutcomeRewards.StrikeoutValue);
                RequireStat(controller, "Env/Aborted Play", 0f);
                float statKmh = Stat(controller, "Pitch/Speed (km/h)");
                Require(statKmh >= range.x - 0.01f && statKmh <= range.y + 0.01f, $"speed stat {statKmh:F1} km/h inside {range}");
                RequireNoStat(controller, "Pitch Type/Four Seam");
                RequireNoStat(controller, "Plate Appearance/On Base");
                return $"PASS stage 1: spread ({spread.x:F2}, {spread.y:F2}) m, sampled zone rate {sampledZoneRate:P1} ({SpreadSamples} samples); " +
                    $"{pitches.Count} four-seamers, {minKmh:F1}-{maxKmh:F1} km/h (range {range.x}-{range.y}), arrival {minArrival:F3}-{maxArrival:F3} s, " +
                    $"max aim offset {maxOffset * 100f:F1} cm, max plate miss {maxMiss * 100f:F2} cm, {calledStrikes} called strikes / {calledBalls} balls; " +
                    $"{plateAppearances} plate appearances ({walks} walks, {strikeouts} strikeouts) match the recounted calls, 0 aborted, " +
                    $"{controller.Stats.RecordCount} stat values.";
            }
            finally
            {
                director.PitchCalled -= OnCalled;
            }
        }

        // ───────────────────────── 2단계 ─────────────────────────

        private static string RunStage2(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            PitcherAgent pitcher = controller.Pitcher;
            BatterAgent batter = controller.Batter;
            BaseballEnvironmentConfig config = director.EnvironmentConfig;
            FieldLayout field = director.FieldLayout;
            BallController ball = UnityEngine.Object.FindAnyObjectByType<BallController>();
            Require(pitcher != null && pitcher.enabled, "pitcher agent passed its initialization checks");
            Require(pitcher.GetComponent<BehaviorParameters>().TeamId != batter.GetComponent<BehaviorParameters>().TeamId,
                "batter and pitcher are on different teams");
            var report = new StringBuilder();
            Vector2 center = director.StrikeZoneCenter;

            // 1) 행동 → 투구 명령 매핑: 구속은 연속 1개, 구종·좌우 칸·높이 칸은 이산 [5, 5, 5].
            BehaviorParameters pitcherBehavior = pitcher.GetComponent<BehaviorParameters>();
            ActionSpec spec = pitcherBehavior.BrainParameters.ActionSpec;
            Require(spec.NumContinuousActions == 1 && spec.BranchSizes != null && spec.BranchSizes.Length == 3 && spec.BranchSizes[0] == PitcherAgent.PitchTypeCount &&
                spec.BranchSizes[1] == PitcherAgent.LocationCells && spec.BranchSizes[2] == PitcherAgent.LocationCells,
                "pitcher behavior: 1 continuous action and discrete branches [5, 5, 5]");
            const int last = PitcherAgent.LocationCells - 1, mid = PitcherAgent.CenterCell;
            PitchTypeProfile curve = config.GetPitchProfile(PitchType.Curve);
            PitchCommand low = PitcherAgent.ToCommand(new ActionBuffers(new[] { -1f }, new[] { 2, mid, mid }), config, center);
            PitchCommand high = PitcherAgent.ToCommand(new ActionBuffers(new[] { 1f }, new[] { 2, last, last }), config, center);
            PitchCommand bad = PitcherAgent.ToCommand(new ActionBuffers(new[] { float.NaN }, new[] { 9, 7, -3 }), config, center);
            float cellWidth = StrikeZone.PlateWidth / 3f, cellHeight = (config.StrikeZoneTop - config.StrikeZoneBottom) / 3f;
            Require(low.Type == PitchType.Curve && Mathf.Abs(low.Speed - curve.MinSpeed) < 1e-4f && low.PlateLocation == center, "min speed at the center cell");
            Require(Mathf.Abs(high.Speed - curve.MaxSpeed) < 1e-4f && Mathf.Abs(high.PlateLocation.x - 2f * cellWidth) < 1e-4f &&
                Mathf.Abs(high.PlateLocation.y - (center.y + 2f * cellHeight + PitcherAgent.TopRowLift)) < 1e-4f, "max speed at the top first-base corner cell (top row lifted)");
            PitchTypeProfile changeup = config.GetPitchProfile(PitchType.Changeup);
            Require(bad.Type == PitchType.Changeup && Mathf.Abs(bad.Speed - 0.5f * (changeup.MinSpeed + changeup.MaxSpeed)) < 1e-4f &&
                bad.PlateLocation == PitcherAgent.CellLocation(last, 0, config, center), "non-finite and out-of-range actions are clamped");
            // 격자: 가운데 3×3 칸만 존에 닿는다(플레이트 앞 모서리 평면에서 공 반지름 포함). 공 가장자리와 존 경계의 가장 작은 여유를 잰다.
            float radius = config.BallRadius, plateHalf = StrikeZone.PlateWidth * 0.5f;
            float innerClearance = float.MaxValue, outerClearance = float.MaxValue;
            for (int column = 0; column < PitcherAgent.LocationCells; column++)
                for (int row = 0; row < PitcherAgent.LocationCells; row++)
                {
                    Vector2 cell = PitcherAgent.CellLocation(column, row, config, center);
                    bool inner = Mathf.Abs(column - mid) <= 1 && Mathf.Abs(row - mid) <= 1;
                    bool touches = StrikeZone.Intersects(new Vector3(cell.x, StrikeZone.PlateDepth, cell.y), radius, config.StrikeZoneBottom, config.StrikeZoneTop);
                    Require(touches == inner, $"cell ({column}, {row}) at {cell} is {(inner ? "inside" : "outside")} the zone");
                    if (inner)
                        innerClearance = Mathf.Min(innerClearance, Mathf.Min(plateHalf - Mathf.Abs(cell.x),
                            Mathf.Min(cell.y - config.StrikeZoneBottom, config.StrikeZoneTop - cell.y)));
                    else
                        outerClearance = Mathf.Min(outerClearance, Mathf.Max(Mathf.Abs(cell.x) - plateHalf,
                            Mathf.Max(config.StrikeZoneBottom - cell.y, cell.y - config.StrikeZoneTop)) - radius);
                }
            report.AppendLine($"PASS pitcher action mapping: 5 types, 5x5 target cells x {-2f * cellWidth:F3}..{2f * cellWidth:F3} m, " +
                $"height {center.y - 2f * cellHeight:F3}..{center.y + 2f * cellHeight + PitcherAgent.TopRowLift:F3} m (top row +{PitcherAgent.TopRowLift * 100f:F0} cm); inner 3x3 centers {innerClearance * 100f:F1}+ cm inside the zone, " +
                $"outer 16 ball edges {outerClearance * 100f:F1}+ cm outside.");

            // 2) 구종별 변화량: 같은 발사 속도에서 회전 없는 공과의 홈플레이트 통과점 차이(Statcast pfx 방식).
            Vector3 origin = field.PitchOriginPosition;
            Vector3 target = new Vector3(field.PitchTargetPosition.x, center.y, field.PitchTargetPosition.z);
            var breaks = new StringBuilder();
            var movement = new Dictionary<PitchType, Vector2>();
            foreach (PitchType type in (PitchType[])Enum.GetValues(typeof(PitchType)))
            {
                PitchTypeProfile profile = config.GetPitchProfile(type);
                float speed = 0.5f * (profile.MinSpeed + profile.MaxSpeed);
                Require(PitchPhysics.TrySolve(origin, target, speed, profile, config, ball.AngularDamping, Time.fixedDeltaTime,
                    out Vector3 velocity, out Vector3 spin, out float arrival, out string reason), $"{type} solves: {reason}");
                bool spunOk = PitchPhysics.TrySimulateCrossing(origin, velocity, spin, target, config, ball.AngularDamping, Time.fixedDeltaTime, out Vector3 spun, out _);
                bool plainOk = PitchPhysics.TrySimulateCrossing(origin, velocity, Vector3.zero, target, config, ball.AngularDamping, Time.fixedDeltaTime, out Vector3 plain, out _);
                Require(spunOk && plainOk, $"{type} simulates");
                Require(Vector3.Distance(spun, target) < 0.002f, $"{type} solution passes the target");
                var pfx = new Vector2(spun.x - plain.x, spun.y - plain.y);
                movement[type] = pfx;
                breaks.Append($"{type} {speed * 3.6f:F0}km/h x{pfx.x * 100f:+0;-0}cm z{pfx.y * 100f:+0;-0}cm t{arrival:F2}s; ");
            }
            // 우투수 기준: 1루 쪽(+X)이 글러브 쪽, 3루 쪽(-X)이 팔 쪽이다. 기본 프로필은 MLB 우투수 평균에 맞췄다(±5 cm).
            var mlb = new Dictionary<PitchType, Vector2>
            {
                [PitchType.FourSeam] = new Vector2(-0.18f, 0.41f), [PitchType.TwoSeam] = new Vector2(-0.38f, 0.20f),
                [PitchType.Curve] = new Vector2(0.23f, -0.25f), [PitchType.Slider] = new Vector2(0.20f, 0.02f),
                [PitchType.Changeup] = new Vector2(-0.36f, 0.18f),
            };
            foreach (var pair in mlb)
                Require(Vector2.Distance(movement[pair.Key], pair.Value) < 0.05f,
                    $"{pair.Key} movement {movement[pair.Key] * 100f} cm is within 5 cm of the MLB average {pair.Value * 100f} cm");
            report.AppendLine("PASS pitch movement vs spinless ball (MLB RHP averages ±5 cm): " + breaks);

            // 3) 중립 행동: 포심 직구, 구속 범위 가운데, 존 중앙. 타자는 지켜봐 스트라이크.
            int pitchesBefore = pitcher.CompletedPitches;
            PitchCommand neutral = default;
            bool thrown = false;
            for (int i = 0; i < 400 && pitcher.CompletedPitches < pitchesBefore + 1; i++)
            {
                BatterAgentVerification.Step(director);
                if (!thrown && director.TryGetLastPitch(out neutral)) thrown = true;
            }
            PitchTypeProfile four = config.GetPitchProfile(PitchType.FourSeam);
            Require(thrown && neutral.Type == PitchType.FourSeam && Mathf.Abs(neutral.Speed - 0.5f * (four.MinSpeed + four.MaxSpeed)) < 1e-3f &&
                Vector2.Distance(neutral.PlateLocation, center) < 1e-4f, "neutral pitcher throws a middle-speed four-seamer at the zone center");
            Require(pitcher.LastPitchReward.Strike == PitcherRewardTracker.StrikeReward && batter.LastPitchReward.Miss < 0f,
                "taken center pitch: pitcher strike reward, batter miss penalty");
            RequirePitchReward(pitcher);
            report.AppendLine($"PASS neutral pitch: {neutral.Type} {neutral.Speed * 3.6f:F1} km/h to zone center, called strike, pitcher {pitcher.LastPitchReward.Total:+0.0;-0.0}, batter {batter.LastPitchReward.Total:+0.0;-0.0}.");

            // 4) 실제 Director 투구: 5구종 × 격자 25칸, 구속 범위 가운데. 목표 통과 오차, 판정(안쪽 3×3 스트라이크, 바깥 16칸 볼),
            //    투수 투구 보상을 확인한다. 바깥 칸이 플레이트 깊이를 지나며 입체 존을 스친 스트라이크는 판정이 존 검사를 따르면
            //    허용하되 비율(MaxOuterCellStrikeRate)을 넘지 않아야 한다.
            float maxMiss = 0f;
            int strikes = 0, balls = 0, outerStrikes = 0;
            var outerStrikeCells = new StringBuilder();
            foreach (PitchType type in (PitchType[])Enum.GetValues(typeof(PitchType)))
            {
                PitchTypeProfile profile = config.GetPitchProfile(type);
                for (int column = 0; column < PitcherAgent.LocationCells; column++)
                    for (int row = 0; row < PitcherAgent.LocationCells; row++)
                    {
                        Vector2 aim = PitcherAgent.CellLocation(column, row, config, center);
                        var command = new PitchCommand(type, 0.5f * (profile.MinSpeed + profile.MaxSpeed), aim);
                        PitchCallSnapshot call = PlayOverriddenPitch(controller, command);
                        Require(call.HasPlateLocation, $"{type} at cell ({column}, {row}) crossed the plate");
                        // 목표는 PitchTarget 평면, 측정은 플레이트 앞 모서리 평면이라 수 mm 차이가 난다.
                        float miss = Vector2.Distance(call.PlateLocation, aim);
                        maxMiss = Mathf.Max(maxMiss, miss);
                        Require(miss < 0.03f, $"{type} at cell ({column}, {row}) crossed within 3 cm (miss {miss:F3} m)");
                        if (Mathf.Abs(column - mid) <= 1 && Mathf.Abs(row - mid) <= 1)
                        {
                            Require(call.Call == PitchCall.CalledStrike && pitcher.LastPitchReward.Strike == PitcherRewardTracker.StrikeReward,
                                $"{type} at inner cell ({column}, {row}) is a called strike (got {call.Call})");
                            strikes++;
                        }
                        else
                        {
                            // 바깥 칸도 공이 플레이트를 지나며 입체 존에 닿으면 규칙상 스트라이크다(내려오는 공이 존 뒤쪽 윗면을 스침).
                            // 판정·보상이 존 통과 검사를 따르면 허용하되, 바깥 칸이 대체로 볼이라는 설계가 유지되는지 비율로 본다.
                            Require(call.Call == (call.InZone ? PitchCall.CalledStrike : PitchCall.Ball),
                                $"{type} at outer cell ({column}, {row}) call follows the zone test (in zone {call.InZone}, got {call.Call})");
                            Require(call.InZone ? pitcher.LastPitchReward.Strike == PitcherRewardTracker.StrikeReward
                                    : pitcher.LastPitchReward.Ball == PitcherRewardTracker.BallPenalty,
                                $"{type} at outer cell ({column}, {row}) pitch reward matches its call");
                            if (!call.InZone) balls++;
                            else
                            {
                                outerStrikes++;
                                outerStrikeCells.Append($"{type} ({column}, {row}); ");
                            }
                        }
                        RequirePitchReward(pitcher);
                    }
            }
            float outerStrikeRate = outerStrikes / (float)(balls + outerStrikes);
            Require(outerStrikeRate <= MaxOuterCellStrikeRate,
                $"outer-cell strikes {outerStrikeRate:P1} stay at or below {MaxOuterCellStrikeRate:P0} ({outerStrikeCells})");
            report.AppendLine($"PASS director pitches: {strikes + balls + outerStrikes} (5 types x 25 cells), max plate miss {maxMiss * 100f:F2} cm, " +
                $"{strikes} called strikes (inner cells), {balls} balls and {outerStrikes} zone-clipping strikes (outer cells), pitch rewards match." +
                (outerStrikes > 0 ? $" Outer strikes: {outerStrikeCells}" : string.Empty));

            // 5) 볼카운트: 볼 4개 = 볼넷(타자 +1, 투수 -1), 스트라이크 3개 = 삼진(타자 -1, 투수 +1). 에피소드는 타석 단위다.
            BatterAgentVerification.FinishCurrentPlateAppearance(controller);
            var ballPitch = new PitchCommand(PitchType.FourSeam, 40f, PitcherAgent.CellLocation(last, mid, config, center));
            for (int i = 0; i < 4; i++) PlayOverriddenPitch(controller, ballPitch);
            Require(controller.LastPlateAppearanceResult == PlateAppearanceResult.Walk &&
                Mathf.Approximately(batter.LastOutcomeReward, PlayOutcomeRewards.WalkValue) &&
                Mathf.Approximately(pitcher.LastOutcomeReward, -PlayOutcomeRewards.WalkValue), "four balls walk the batter with mirrored outcome rewards");
            RequireEpisodeReward(batter.LastCompletedCumulativeReward, batter.LastPlateAppearanceAddedReward, batter.LastOutcomeReward, "batter walk");
            RequireEpisodeReward(pitcher.LastCompletedCumulativeReward, pitcher.LastPlateAppearanceAddedReward, pitcher.LastOutcomeReward, "pitcher walk");
            RequireStat(controller, "Plate Appearance/Walk", 1f);
            RequireStat(controller, "Plate Appearance/Pitches", 4f);
            RequireStat(controller, "Pitch Call/Ball", 1f);
            RequireStat(controller, "Plate Discipline/Zone Rate", 0f);
            RequireStat(controller, "Plate Discipline/Chase Rate", 0f);
            RequireStat(controller, "Pitch Type/Four Seam", 1f);
            RequireStat(controller, "Pitch Type/Curve", 0f);
            RequireStat(controller, "Matchup/Batter Win", 1f);
            RequireStat(controller, "Matchup/Pitcher Win", 0f);
            RequireStat(controller, "Matchup/Draw", 0f);
            float walkPitcher = pitcher.LastCompletedCumulativeReward;
            var strikePitch = new PitchCommand(PitchType.FourSeam, 40f, center);
            for (int i = 0; i < 3; i++) PlayOverriddenPitch(controller, strikePitch);
            Require(controller.LastPlateAppearanceResult == PlateAppearanceResult.Strikeout &&
                Mathf.Approximately(batter.LastOutcomeReward, -PlayOutcomeRewards.StrikeoutValue) &&
                Mathf.Approximately(pitcher.LastOutcomeReward, PlayOutcomeRewards.StrikeoutValue), "three strikes are a strikeout with mirrored outcome rewards");
            RequireEpisodeReward(pitcher.LastCompletedCumulativeReward, pitcher.LastPlateAppearanceAddedReward, pitcher.LastOutcomeReward, "pitcher strikeout");
            RequireStat(controller, "Plate Appearance/Strikeout", 1f);
            RequireStat(controller, "Plate Appearance/Pitches", 3f);
            RequireStat(controller, "Matchup/Pitcher Win", 1f);
            RequireStat(controller, "Matchup/Batter Win", 0f);
            report.AppendLine($"PASS count: walk episode pitcher {walkPitcher:+0.00;-0.00} / batter {+PlayOutcomeRewards.WalkValue:+0.0}, " +
                $"strikeout episode pitcher {pitcher.LastCompletedCumulativeReward:+0.00;-0.00} / batter {batter.LastCompletedCumulativeReward:+0.00;-0.00}.");

            // 6) 타자가 맞히면 투수는 접촉 감점을 받고, 인플레이로 타석이 끝난다(수비가 없어 결과 보상 0).
            PitchCallSnapshot hitCall = PlayOverriddenPitch(controller, new PitchCommand(PitchType.FourSeam, 38f, center), swing: true);
            Require(batter.LastPitchReward.Contact > 0f && pitcher.LastPitchReward.Contact == PitcherRewardTracker.ContactPenalty,
                $"contact costs the pitcher (call {hitCall.Call})");
            RequirePitchReward(pitcher);
            Require(controller.LastPlateAppearanceResult == PlateAppearanceResult.InPlay && batter.LastOutcomeReward == 0f,
                "ball in play ends the plate appearance with no outcome reward without fielders");
            RequireStat(controller, "Pitch Call/In Play", 1f);
            RequireStat(controller, "Plate Discipline/Swing Rate", 1f);
            RequireStat(controller, "Plate Discipline/Zone Swing Rate", 1f);
            RequireStat(controller, "Plate Discipline/Contact Rate", 1f);
            RequireStat(controller, "Batter Reward/Contact", BatterRewardTracker.ContactReward);
            RequireStat(controller, "Batted Ball/Fair", 1f);
            RequireStat(controller, "Plate Appearance/In Play", 1f);
            RequireStat(controller, "Matchup/Draw", 1f);
            float exitKmh = Stat(controller, "Batted Ball/Exit Speed (km/h)");
            float timingMs = Stat(controller, "Swing/Timing Error (ms)");
            Require(exitKmh > 0f, "exit speed stat recorded for contact");
            report.AppendLine($"PASS contact: batter pitch {batter.LastPitchReward.Total:+0.000;-0.000}, pitcher pitch {pitcher.LastPitchReward.Total:+0.000;-0.000} (call {hitCall.Call}), in play, " +
                $"stats exit {exitKmh:F1} km/h, launch {Stat(controller, "Batted Ball/Launch Angle (deg)"):F1} deg, timing {timingMs:+0;-0} ms.");
            report.AppendLine($"PASS stage 2 TensorBoard stats: {controller.Stats.RecordCount} values recorded.");
            Require(controller.AbortedPlays == 0, "no aborted plays");
            return report.ToString();
        }

        /// <summary>
        /// 다음 투구에서 투수의 중립 결정 직후 같은 명령 경계로 <paramref name="command"/>를 덮어쓴다.
        /// <paramref name="swing"/>이면 자세를 목표 높이에 맞추고 도착 시각에 맞춰 스윙 행동을 넣는다. 투구가 끝날 때의 판정을 돌려준다.
        /// </summary>
        private static PitchCallSnapshot PlayOverriddenPitch(TrainingEnvController controller, PitchCommand command, bool swing = false)
        {
            PlayDirector director = controller.Director;
            PitcherAgent pitcher = controller.Pitcher;
            BatterAgent batter = controller.Batter;
            BaseballEnvironmentConfig config = director.EnvironmentConfig;
            float centerPhase = config.SwingStartDegrees / (config.SwingStartDegrees - config.SwingEndDegrees);
            for (int i = 0; i < 1500 && !(director.State == PlayState.Ready && pitcher.PitchActive && !pitcher.PitchSubmitted && controller.PlayActive); i++)
                BatterAgentVerification.Step(director);
            int before = pitcher.CompletedPitches;
            bool overridden = false, setup = false, swung = false;
            PitchCallSnapshot call = default;
            for (int i = 0; i < 1500 && pitcher.CompletedPitches < before + 1; i++)
            {
                Unity.MLAgents.Academy.Instance.EnvironmentStep();
                if (swing && !setup && director.State == PlayState.Ready && batter.SetupSubmitted)
                {
                    director.RequestBatterSetup(new BatterSetupCommand(Vector2.zero,
                        new Vector3(0f, command.PlateLocation.y - director.FieldLayout.PitchTargetPosition.y, 0f)));
                    setup = true;
                }
                if (!overridden && director.State == PlayState.Ready && pitcher.PitchSubmitted)
                {
                    director.RequestThrowPitch(command);
                    overridden = true;
                }
                if (swing && !swung && director.State == PlayState.PitchInFlight && director.PitchArrivalSeconds > 0f &&
                    director.GetSnapshot().ElapsedSeconds >= director.PitchArrivalSeconds - centerPhase * config.SwingDuration)
                {
                    batter.OnActionReceived(new ActionBuffers(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.125f }, new[] { 1 }));
                    swung = true;
                }
                BatterAgentVerification.Advance(director);
                if (director.GetPitchCall().Call != PitchCall.None) call = director.GetPitchCall();
            }
            Require(overridden && pitcher.CompletedPitches == before + 1, $"pitch {command.Type} played to the end of the pitch");
            // 3단계는 플레이가 끝나 컨트롤러가 초기화를 요청할 때까지 기다린다.
            for (int i = 0; i < 1500 && director.State != PlayState.Ready; i++) BatterAgentVerification.Step(director);
            return call;
        }

        private static void RequirePitchReward(PitcherAgent pitcher)
        {
            PitcherRewardSnapshot reward = pitcher.LastPitchReward;
            Require(reward.Complete && Mathf.Abs(reward.Total - pitcher.LastPitchAddedReward) < 0.0001f,
                "pitcher pitch reward equals tracker total");
        }

        private static void RequireEpisodeReward(float cumulative, float pitchRewards, float outcome, string label) =>
            Require(Mathf.Abs(cumulative - (pitchRewards + outcome)) < 1e-4f, $"{label}: episode reward = pitch rewards + outcome");

        // ───────────────────────── 3단계 ─────────────────────────

        private static string RunStage3(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            Require(controller.Runners.Length == 4 && director.RunnerSlotCount == 4, "four runner slots (batter-runner + three base runners)");
            foreach (RunnerAgent runner in controller.Runners) Require(runner.enabled, $"runner {runner.name} passed its checks");
            Require(controller.Fielders.Length == FielderAgent.RoleCount && director.FielderCount == FielderAgent.RoleCount, "nine fielders wired to the director");
            for (int i = 0; i < director.FielderCount; i++)
                Require((int)director.GetFielder(i).Role == i, $"fielder {i} plays role {(FielderRole)i}");
            foreach (FielderAgent fielder in controller.Fielders)
                Require(fielder.enabled && director.GetFielder(fielder.FielderIndex) == fielder.GetComponent<FielderController>(), $"fielder {fielder.name} passed its checks");
            var report = new StringBuilder();
            Require(controller.AbortedPlays == 0, "no aborted plays before scenarios");
            throwsChecked = 0;
            throwMaskOpen = 0;

            // 중립 행동: 존 중앙 스트라이크 3개로 삼진. 수비·주자 에피소드는 생기지 않는다.
            int plays = controller.CompletedPlays;
            for (int i = 0; i < 2500 && controller.CompletedPlays < plays + 3; i++) BatterAgentVerification.Step(director);
            Require(controller.CompletedPlays >= plays + 3 && !controller.LastPlayHadFielding && controller.LastPlateAppearanceResult == PlateAppearanceResult.Strikeout,
                "neutral pitches end in a strikeout without fielding");
            report.AppendLine($"PASS neutral stage 3: 3 pitches, strikeout, no fielding episodes.");

            VerifyFielderMovement(controller);
            report.AppendLine($"PASS fielder movement: action direction, move gain x{FielderAgent.MoveActionGain:0} (output 1/3 = full speed), unit-circle speed limit, " +
                "small/zero/non-finite inputs and reset for all nine fielders.");
            VerifyPositionDuties(controller);
            report.AppendLine("PASS position duties: role table (C home, 1B first, 3B third, 2B/SS second by ball side and backup, P first while 1B chases, " +
                "zones 6/12 m), 3 m base allowance, distance/time scaling, 0.1/s cap, cover potential only for base duties, chaser exemption, inactive state.");
            FieldLayout field = director.FieldLayout;
            var empty = new Situation(false, false, false, 0);
            int firstBase = FielderIndexOf(director, FielderRole.FirstBase);
            string cover1B = TrainingStats.BaseCoverKey(BaseId.First);
            string cover2B = TrainingStats.BaseCoverKey(BaseId.Second);
            string cover3B = TrainingStats.BaseCoverKey(BaseId.Third);
            int cover2BCount = controller.Stats.CountOf(cover2B), cover3BCount = controller.Stats.CountOf(cover3B);

            var a = PlayScenario(controller, empty, ExitVelocity(-13.8f, -6f, 28f), null);
            Require(a.play.EndReason == PitchEndReason.ForceOut && a.play.Outs == 1, $"A grounder to short is a force out at first (got {Describe(a.play)})");
            RequireOutcomeRewards(controller);
            RequireStat(controller, "Plate Appearance/In Play", 1f);
            RequireStat(controller, "Plate Appearance/On Base", 0f);
            RequireStat(controller, "Play/Outs", 1f);
            RequireStat(controller, "Play End/Force Out", 1f);
            RequireStat(controller, "Play End/Runner Safe", 0f);
            RequireStat(controller, "Matchup/Pitcher Win", 1f);
            Require(Stat(controller, "Play/Live Time (s)") > 0f, "live time stat recorded");
            RequireStat(controller, "Defense/Fielded", 1f);
            RequireStat(controller, "Defense Reward/Outcome", controller.LastDefenseOutcomeReward);
            RequireStat(controller, "Defense Reward/Shaping", controller.LastDefenseShapingReward);
            RequireStat(controller, "Defense Reward/Chase Shaping", controller.LastChaseShapingReward);
            RequireStat(controller, "Defense Reward/Fielding", PlayOutcomeRewards.FieldingReward);
            Require(a.fieldedDecision > 0 && a.potentials[a.fieldedDecision] - a.potentials[a.fieldedDecision - 1] > 0.8f * PlayOutcomeRewards.DefenseFieldedValue,
                "first fielding raises the defense potential by about the fielded value");
            report.AppendLine($"PASS A grounder to SS: {Describe(a.play)} at {a.seconds:F2} s, fielded by {a.fielder}, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}, batter {controller.LastBatterOutcomeReward:+0.00;-0.00}.");
            report.AppendLine($"PASS A defense shaping: potential {a.potentials[0]:+0.000;-0.000} at contact, " +
                $"{a.potentials[a.fieldedDecision - 1]:+0.000;-0.000} → {a.potentials[a.fieldedDecision]:+0.000;-0.000} at the first fielding, " +
                $"play sum {controller.LastDefenseShapingReward:+0.000;-0.000} over {a.potentials.Count} decisions.");
            // 역할 임무 보조 보상: 타자주자가 1루로 달리는 동안 1루수가 1루에 있었다. 2·3루로 향한 주자가 없어 그 지표는 기록되지 않는다.
            RequireStat(controller, cover1B, 1f);
            Require(controller.Stats.CountOf(cover2B) == cover2BCount && controller.Stats.CountOf(cover3B) == cover3BCount,
                "A records no 2B/3B cover because no runner heads there");
            report.AppendLine($"PASS A role duty shaping: cover Φ_i at contact {DescribeCover(director, a.cover[0])}, play sums {DescribeCoverSums(controller)}; " +
                $"role table holds at all {a.cover.Count} decisions, {cover1B} = 1, fielding reward {PlayOutcomeRewards.FieldingReward:+0.0} to {a.fielder} only, " +
                $"position penalty sum {controller.LastPositionReward:+0.000;-0.000}.");

            var b = PlayScenario(controller, empty, ExitVelocity(-19f, 32f, 34f), null);
            Require(b.play.EndReason == PitchEndReason.FlyOut && b.play.Outs == 1, $"B fly ball to left field is caught (got {Describe(b.play)})");
            RequireOutcomeRewards(controller);
            report.AppendLine($"PASS B fly to left: {Describe(b.play)} at {b.seconds:F2} s by {b.fielder}, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}.");

            // 투수·내야수 머리 위로 넘어가 좌중간 외야에 떨어지는 안타. 중견수가 정면에 서 있지 않아 제자리 수비는 잡지 못한다.
            Vector3 single = ExitVelocity(SingleSprayDegrees, SingleLaunchDegrees, SingleSpeed);
            var c = PlayScenario(controller, empty, single, null);
            Require(c.play.EndReason == PitchEndReason.RunnerSafe && c.play.BatterBases == 1 && c.situation.OnFirst,
                $"C single to left-center ends safe at first (got {Describe(c.play)})");
            RequireOutcomeRewards(controller);
            RequireStat(controller, "Plate Appearance/On Base", 1f);
            RequireStat(controller, "Play/Batter Bases", 1f);
            RequireStat(controller, "Play End/Runner Safe", 1f);
            RequireStat(controller, "Matchup/Batter Win", 1f);
            RequireStat(controller, cover1B, 1f);
            report.AppendLine($"PASS C single to left-center: {Describe(c.play)} at {c.seconds:F2} s, fielded by {c.fielder}, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}, batter {controller.LastBatterOutcomeReward:+0.00;-0.00}.");

            // 보조 보상 방향: 같은 타구에서 공을 쫓고 1루를 덮는 스크립트 수비가 제자리 수비보다 첫 포구 전까지 보조 보상을 더 받는다.
            var still = PlayScenario(controller, empty, single, null, scriptedDefense: false);
            RequireStat(controller, "Defense/Fielded", 0f);
            RequireStat(controller, "Defense Reward/Fielding", 0f);
            int window = c.fieldedDecision - 1;
            Require(window >= 1 && still.potentials.Count > window && Mathf.Approximately(c.potentials[0], still.potentials[0]),
                "C and the standing-still replay start from the same potential");
            float chasing = ShapingSum(SumSamples(c.chase), window), standing = ShapingSum(SumSamples(still.chase), window);
            Require(chasing > standing + 0.01f, $"chasing earns more shaping before the first fielding than standing still ({chasing:+0.000} vs {standing:+0.000})");
            report.AppendLine($"PASS shaping direction on C's ball: first {window} decisions chasing {chasing:+0.000;-0.000} vs standing still {standing:+0.000;-0.000}; " +
                $"standing still ends {Describe(still.play)}, play sum {controller.LastDefenseShapingReward:+0.000;-0.000}.");
            // 1루수 개인 보조 보상 방향: 1루로 뛰어가는 쪽이 제자리보다 첫 1초 동안 더 받는다. 제자리 1루수는 1루를 밟지 못한다.
            RequireStat(controller, cover1B, 0f);
            int coverWindow = Mathf.Min(window, 10);
            float coverRunning = ShapingSum(Column(c.cover, firstBase), coverWindow);
            float coverStanding = ShapingSum(Column(still.cover, firstBase), coverWindow);
            Require(coverRunning > coverStanding + 0.03f,
                $"1B running to first earns more cover shaping than standing still ({coverRunning:+0.000} vs {coverStanding:+0.000})");
            report.AppendLine($"PASS 1B cover direction on C's ball: first {coverWindow} decisions running to first {coverRunning:+0.000;-0.000} " +
                $"vs standing still {coverStanding:+0.000;-0.000}; {cover1B} 1 vs 0; position penalty sum scripted {c.positionRewards.Sum():+0.000;-0.000} " +
                $"vs standing {still.positionRewards.Sum():+0.000;-0.000}.");

            var d = PlayScenario(controller, empty, single,
                (slot, r) => slot == 0 && r.Phase == RunnerPhase.Holding && r.LastTouchedBase == BaseId.First ? RunnerDecision.Advance : (RunnerDecision?)null);
            Require((d.play.EndReason == PitchEndReason.TagOut && d.play.Outs == 1) || (d.play.EndReason == PitchEndReason.RunnerSafe && d.play.BatterBases == 2),
                $"D stretch for second ends in a tag out or safe at second (got {Describe(d.play)})");
            RequireOutcomeRewards(controller);
            report.AppendLine($"PASS D stretch to second: {Describe(d.play)} at {d.seconds:F2} s, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}.");

            var e = PlayScenario(controller, empty, ExitVelocity(-52f, 70f, 22f), null);
            Require(e.play.EndReason == PitchEndReason.FlyOut && !field.IsFairGroundPoint(e.catchPoint), $"E foul pop is caught in foul ground (got {Describe(e.play)} at {e.catchPoint})");
            RequireOutcomeRewards(controller);
            report.AppendLine($"PASS E foul pop: {Describe(e.play)} by {e.fielder} at ({e.catchPoint.x:F1}, {e.catchPoint.z:F1}), foul ground.");

            // F) 1루 주자, 유격수 땅볼: 2루 포스(유격수가 직접 밟음) → 1루 송구. 병살 또는 한 개 아웃.
            var f = PlayScenario(controller, new Situation(true, false, false, 0), ExitVelocity(-13.8f, -6f, 28f), null);
            Require(f.play.Outs >= 1 && f.play.Runs == 0 &&
                (f.play.Outs == 2 ? f.play.EndReason == PitchEndReason.ForceOut && !f.situation.OnFirst && !f.situation.OnSecond
                    : f.play.EndReason == PitchEndReason.RunnerSafe && f.situation.OnFirst && !f.situation.OnSecond),
                $"F runner on first, grounder to short: force outs with consistent bases (got {Describe(f.play)}, {Describe(f.situation)})");
            RequireOutcomeRewards(controller);
            // 1루 주자가 2루로 밀려나므로 유격수가 2루를, 타자주자 때문에 1루수가 1루를 밟는다.
            RequireStat(controller, cover2B, 1f);
            RequireStat(controller, cover1B, 1f);
            report.AppendLine($"PASS F role duty shaping: play sums {DescribeCoverSums(controller)}, {cover2B} = 1, {cover1B} = 1.");
            report.AppendLine($"PASS F runner on 1B, grounder to SS: {Describe(f.play)}, then {Describe(f.situation)}, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}, batter {controller.LastBatterOutcomeReward:+0.00;-0.00}.");

            // G) 3루 주자 1아웃, 좌익수 뜬공: 포구 후 태그업. 포수가 홈을 맡으므로 홈인(희생플라이) 또는 홈 태그 아웃(3아웃)이다.
            var g = PlayScenario(controller, new Situation(false, false, true, 1), ExitVelocity(-19f, 32f, 34f),
                (slot, r) => slot == 3 && director.BattedBallFielded && r.Phase == RunnerPhase.Holding && r.LastTouchedBase == BaseId.Third
                    ? RunnerDecision.Advance : (RunnerDecision?)null);
            bool sacrificeFly = g.play.Outs == 1 && g.play.Runs == 1 && g.situation.Outs == 2 && g.situation.RunsThisHalfInning == 1;
            bool thrownOutAtHome = g.play.Outs == 2 && g.play.Runs == 0 && g.play.RunnerOuts == 1 && g.play.EndReason == PitchEndReason.TagOut;
            Require(g.first.Contains("(air)") && (sacrificeFly || thrownOutAtHome),
                $"G fly with runner on third: catch, tag up, then score or tagged out at home (got {Describe(g.play)}, {Describe(g.situation)})");
            RequireOutcomeRewards(controller);
            RequireStat(controller, "Play/Runs", g.play.Runs);
            RequireStat(controller, "Play/Outs", g.play.Outs);
            report.AppendLine($"PASS G tag-up from third ({(sacrificeFly ? "sac fly" : "thrown out at home")}): {Describe(g.play)}, then {Describe(g.situation)}, batter {controller.LastBatterOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}.");

            // H) 만루 볼넷은 밀어내기 1점.
            SetSituation(controller, new Situation(true, true, true, 0));
            var ballPitch = new PitchCommand(PitchType.FourSeam, 40f, PitcherAgent.CellLocation(PitcherAgent.LocationCells - 1, PitcherAgent.CenterCell,
                director.EnvironmentConfig, director.StrikeZoneCenter));
            for (int i = 0; i < 4; i++) PlayOverriddenPitch(controller, ballPitch);
            SituationSnapshot afterWalk = director.GetSituation();
            Require(controller.LastPlateAppearanceResult == PlateAppearanceResult.Walk && afterWalk.RunsThisHalfInning == 1 &&
                afterWalk.OnFirst && afterWalk.OnSecond && afterWalk.OnThird && Mathf.Approximately(controller.LastBatterOutcomeReward, PlayOutcomeRewards.WalkValue),
                $"H bases-loaded walk forces in a run (got {Describe(afterWalk)})");
            RequireStat(controller, "Plate Appearance/Walk", 1f);
            RequireStat(controller, "Plate Appearance/On Base", 1f);
            report.AppendLine($"PASS H bases-loaded walk: {Describe(afterWalk)}, batter {controller.LastBatterOutcomeReward:+0.0}.");

            // I) 2아웃 1루에서 삼진이면 반 이닝이 끝나고 다음 타석은 주자 없이 0아웃이다.
            SetSituation(controller, new Situation(true, false, false, 2));
            Require(director.GetRunnerSnapshot(1).Phase == RunnerPhase.Holding, "runner placed on first");
            var strikePitch = new PitchCommand(PitchType.FourSeam, 40f, director.StrikeZoneCenter);
            for (int i = 0; i < 3; i++) PlayOverriddenPitch(controller, strikePitch);
            Require(controller.LastPlateAppearanceResult == PlateAppearanceResult.Strikeout, "I strikeout");
            RequireStat(controller, "Half Inning/Runs", 0f);
            RequireStat(controller, "Plate Appearance/On Base", 0f);
            BatterAgentVerification.FinishCurrentPlateAppearance(controller);
            SituationSnapshot newInning = director.GetSituation();
            Require(newInning.Outs == 0 && !newInning.OnFirst && newInning.RunsThisHalfInning == 0 && director.GetRunnerSnapshot(1).Phase == RunnerPhase.Inactive,
                $"I third out clears the half-inning (got {Describe(newInning)})");
            report.AppendLine($"PASS I two-out strikeout: new half-inning {Describe(newInning)}.");

            // J) 2루 주자, C와 같은 안타: 주자가 멈출 때마다 진루한다. 타자는 1루. 포수가 홈을 맡으므로 홈인 또는 홈 태그 아웃이다.
            var j = PlayScenario(controller, new Situation(false, true, false, 0), single,
                (slot, r) => slot == 2 && r.Phase == RunnerPhase.Holding ? RunnerDecision.Advance : (RunnerDecision?)null);
            bool rbi = j.play.Runs == 1 && j.play.Outs == 0 && j.situation.RunsThisHalfInning == 1;
            bool outAtHome = j.play.Runs == 0 && j.play.Outs == 1 && j.play.RunnerOuts == 1 && j.situation.Outs == 1;
            Require((rbi || outAtHome) && j.play.BatterBases >= 1 && j.situation.OnFirst != (j.play.BatterBases >= 2) && !j.situation.OnThird,
                $"J runner on second scores or is thrown out at home on a single (got {Describe(j.play)}, {Describe(j.situation)})");
            RequireOutcomeRewards(controller);
            report.AppendLine($"PASS J single with runner on second ({(rbi ? "RBI" : "thrown out at home")}): {Describe(j.play)}, then {Describe(j.situation)}, batter {controller.LastBatterOutcomeReward:+0.00;-0.00}, runners {controller.LastRunnerOutcomeReward:+0.00;-0.00}, defense {controller.LastDefenseOutcomeReward:+0.00;-0.00}.");

            // K) 1루 주자, 유격수 뜬공: 포스로 달리던 주자는 리터치해야 한다. 1루로 송구하면 더블 아웃(늦으면 한 개).
            var k = PlayScenario(controller, new Situation(true, false, false, 0), ExitVelocity(-13.8f, 55f, 22f), null);
            Require(k.play.Outs >= 1 && (k.play.Outs == 1 ? k.situation.OnFirst : !k.situation.OnFirst) && k.first.Contains("(air)"),
                $"K pop-up with runner on first: caught, runner doubled off or back safely (got {Describe(k.play)}, {Describe(k.situation)})");
            RequireOutcomeRewards(controller);
            report.AppendLine($"PASS K pop-up, runner must retouch: {Describe(k.play)}, then {Describe(k.situation)}, caught by {k.fielder}.");

            Require(controller.AbortedPlays == 0, "no aborted plays");
            RequireStat(controller, "Env/Aborted Play", 0f);
            Require(throwsChecked > 0 && throwMaskOpen > 0, "scenarios include throws and decisions with the throw options open");
            report.AppendLine($"PASS throw mask: throw options masked at every fielder decision except {throwMaskOpen} ball-holder decisions; " +
                $"{throwsChecked} scripted throws went out from the holder.");
            report.AppendLine($"PASS stage 3 TensorBoard stats: play, plate-appearance and half-inning values match the scenarios ({controller.Stats.RecordCount} values).");
            return report.ToString();
        }

        private readonly struct Situation
        {
            public Situation(bool first, bool second, bool third, int outs) { First = first; Second = second; Third = third; Outs = outs; }
            public bool First { get; }
            public bool Second { get; }
            public bool Third { get; }
            public int Outs { get; }
        }

        private static string Describe(PlaySummary p) =>
            $"{p.EndReason} outs {p.Outs} runs {p.Runs} batter bases {p.BatterBases} runner outs {p.RunnerOuts} advanced {p.BasesAdvanced}";

        private static string Describe(SituationSnapshot s) =>
            $"O{s.Outs} {(s.OnFirst ? "1" : "-")}{(s.OnSecond ? "2" : "-")}{(s.OnThird ? "3" : "-")} R{s.RunsThisHalfInning}";

        private static Vector3 ExitVelocity(float sprayDegrees, float launchDegrees, float speed) =>
            BatterController.ComputeExitVelocity(sprayDegrees, launchDegrees, speed);

        /// <summary>새 타석의 Ready에서 누상 주자·아웃 상황을 정하고 적용될 때까지 한 고정 단계 진행한다.</summary>
        private static void SetSituation(TrainingEnvController controller, Situation situation)
        {
            PlayDirector director = controller.Director;
            BatterAgentVerification.FinishCurrentPlateAppearance(controller);
            director.RequestSetSituation(situation.First, situation.Second, situation.Third, situation.Outs);
            BatterAgentVerification.Advance(director);
            SituationSnapshot applied = director.GetSituation();
            Require(applied.OnFirst == situation.First && applied.OnSecond == situation.Second && applied.OnThird == situation.Third &&
                applied.Outs == situation.Outs, "situation applied");
        }

        /// <summary>
        /// 새 타석에서 상황을 정한 뒤 시나리오 타구를 시작하고, 매 Academy 단계 뒤 스크립트 수비·주루 명령으로 중립 행동을 덮어써
        /// 플레이가 끝날 때까지 진행한다(<paramref name="scriptedDefense"/>가 false면 수비는 중립 행동으로 제자리에 선다).
        /// 수비 결정 시점마다 포텐셜을 따로 표본으로 모아 컨트롤러의 보조 보상 합과 비교한다.
        /// 결과 요약, 다음 타석 시작 상황, 결정 시점 포텐셜과 첫 포구 뒤 첫 결정의 번호(없으면 -1)를 돌려준다.
        /// </summary>
        private static (PlaySummary play, SituationSnapshot situation, float seconds, string fielder, string first, Vector3 catchPoint,
            List<float> potentials, int fieldedDecision, List<float[]> cover, List<float[]> chase, float[] positionRewards) PlayScenario(
            TrainingEnvController controller, Situation situation, Vector3 exitVelocity, Func<int, RunnerSnapshot, RunnerDecision?> runnerPolicy,
            bool scriptedDefense = true)
        {
            PlayDirector director = controller.Director;
            SetSituation(controller, situation);
            string firstFielder = "none";
            Vector3 firstCatch = Vector3.zero;
            var potentials = new List<float>();
            var cover = new List<float[]>();
            var chase = new List<float[]>();
            var positionRewards = new float[director.FielderCount];
            var fieldingRewards = new float[director.FielderCount];
            int fieldedDecision = -1;
            int fieldSteps = 0;
            void OnFielded(int index, bool inAir)
            {
                if (firstFielder != "none") return;
                firstFielder = director.GetFielder(index).name + (inAir ? " (air)" : "");
                fieldingRewards[index] += PlayOutcomeRewards.FieldingReward;
                firstCatch = director.GetSnapshot().BallPosition;
            }
            director.BallFielded += OnFielded;
            try
            {
                director.RequestScriptedBattedBall(exitVelocity);
                BatterAgentVerification.Advance(director);
                Require(director.State == PlayState.BattedBallInFlight, "scripted batted ball started: " + director.GetSnapshot().LastRejectionReason);
                int before = controller.CompletedPlays;
                float seconds = 0f;
                for (int i = 0; i < 1500 && controller.CompletedPlays == before; i++)
                {
                    Unity.MLAgents.Academy.Instance.EnvironmentStep();
                    if (director.State == PlayState.BattedBallInFlight)
                    {
                        float[] positionStep = SamplePositionRewards(director);
                        if (fieldSteps > 0)
                            for (int f = 0; f < positionRewards.Length; f++) positionRewards[f] += positionStep[f];
                        // Director 고정 단계 전이라 컨트롤러가 이번 단계 결정 직전에 본 상태와 같다.
                        if (fieldSteps++ % controller.FieldDecisionInterval == 0)
                        {
                            potentials.Add(SampleGroupPotential(director));
                            cover.Add(SampleCoverPotentials(director));
                            chase.Add(SampleChasePotentials(director));
                            // Agent 누적 개인 보상에는 그룹 보상이 없다. 개인 차분·자리 유지 감점·포구 보상만 지급됐는지 확인한다.
                            for (int j = 0; j < controller.Fielders.Length; j++)
                            {
                                int f = controller.Fielders[j].FielderIndex;
                                float expected = ShapingSum(Column(chase, f), chase.Count - 1) + ShapingSum(Column(cover, f), cover.Count - 1) +
                                    positionRewards[f] + fieldingRewards[f];
                                Require(Mathf.Abs(controller.Fielders[j].GetCumulativeReward() - expected) < 1e-4f,
                                    $"{controller.Fielders[j].name} receives only its own chase, cover shaping, position and fielding rewards");
                            }
                            if (fieldedDecision < 0 && director.BattedBallFielded) fieldedDecision = potentials.Count - 1;
                        }
                        if (fieldSteps % controller.FieldDecisionInterval == 1) RequireThrowMasks(controller);
                        if (scriptedDefense) ScriptedDefense(director);
                        for (int slot = 0; slot < director.RunnerSlotCount && runnerPolicy != null; slot++)
                        {
                            RunnerDecision? decision = runnerPolicy(slot, director.GetRunnerSnapshot(slot));
                            if (decision.HasValue) director.RequestRunnerDecision(slot, decision.Value);
                        }
                    }
                    if (director.State == PlayState.Ended && seconds == 0f) seconds = director.GetSnapshot().ElapsedSeconds;
                    int holderBefore = director.BallHolder;
                    BatterAgentVerification.Advance(director);
                    if (holderBefore >= 0 && director.LastThrower == holderBefore && director.BallHolder != holderBefore) throwsChecked++;
                }
                Require(controller.CompletedPlays == before + 1 && controller.LastPlayHadFielding, "scenario play finished with fielding");
                PlaySummary play = controller.LastPlaySummary;
                Require(play.Resolved, "play resolved");
                RequireDefenseShaping(controller, potentials);
                RequireCoverShaping(controller, cover);
                RequireChaseShaping(controller, chase);
                RequirePositionRewards(controller, positionRewards);
                RequireFieldingRewards(controller, fieldingRewards);
                BatterAgentVerification.FinishCurrentPlateAppearance(controller);
                return (play, director.GetSituation(), seconds, firstFielder, firstFielder, firstCatch, potentials, fieldedDecision, cover, chase, positionRewards);
            }
            finally
            {
                director.BallFielded -= OnFielded;
            }
        }

        private static int throwsChecked;
        private static int throwMaskOpen;

        private sealed class MaskRecorder : IDiscreteActionMask
        {
            public readonly bool[] Enabled = { true, true, true, true, true };
            public void SetActionEnabled(int branch, int actionIndex, bool isEnabled)
            {
                if (branch == 0) Enabled[actionIndex] = isEnabled;
            }
        }

        /// <summary>
        /// 결정 시점의 송구 마스크: 송구 안 함(0)은 늘 허용하고, 송구(1~4)는 그 수비수가 공을 쥐었을 때만 허용한다.
        /// Academy 단계(결정·마스크 수집) 직후, Director 고정 단계 전이라 같은 상태를 본다.
        /// </summary>
        private static void RequireThrowMasks(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            foreach (FielderAgent agent in controller.Fielders)
            {
                var mask = new MaskRecorder();
                agent.WriteDiscreteActionMask(mask);
                bool expected = director.State == PlayState.BattedBallInFlight && director.BallHolder == agent.FielderIndex;
                Require(mask.Enabled[0], $"{agent.name} may always choose not to throw");
                for (int target = 1; target < FielderAgent.ThrowTargetCount; target++)
                    Require(mask.Enabled[target] == expected, $"{agent.name} throw option {target} is enabled only while holding the ball");
                if (expected) throwMaskOpen++;
            }
        }

        /// <summary>
        /// 검증용 단순 수비. 공을 가진 수비수는 ① 아직 도착하지 않은 가장 앞선 포스 베이스가 12 m 안이면 직접 밟고, 멀면 그 베이스로 던진다.
        /// ② 포스가 없으면 리터치해야 하는 주자의 원래 베이스, ③ 없으면 달리는 주자가 향하는 베이스로 던진다(1루수는 1루를 밟는다).
        /// 공이 살아 있으면 예상 지점에 가장 가까운 수비수가 쫓는다. 포수는 홈, 1루수는 1루(1루수가 공을 처리하면 투수가 1루),
        /// 주자가 2루로 가거나 1루 주자가 포스일 때 2루는 3루 쪽 타구면 2루수·1루 쪽이면 유격수(그 수비수가 공을 처리하면 다른 쪽),
        /// 주자가 3루로 가거나 2루 주자가 포스일 때 3루수는 3루를 맡는다. 나머지는 제자리에 선다.
        /// </summary>
        private static void ScriptedDefense(PlayDirector director)
        {
            FieldLayout field = director.FieldLayout;
            PitchSnapshot pitch = director.GetSnapshot();
            int holder = director.BallHolder;
            bool toSecond = false, toThird = false;
            for (int slot = 0; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot r = director.GetRunnerSnapshot(slot);
                if (!r.IsLive) continue;
                toSecond |= r.NextBase == BaseId.Second && r.Phase != RunnerPhase.Holding;
                toThird |= r.NextBase == BaseId.Third && r.Phase != RunnerPhase.Holding;
            }
            Vector3 chase = PredictChasePoint(pitch.BallPosition, pitch.BallVelocity);
            int chaser = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < director.FielderCount && holder < 0; i++)
            {
                float distance = Flat(director.GetFielder(i).Position - chase).magnitude;
                if (distance < best) { best = distance; chaser = i; }
            }
            bool Handling(FielderRole role)
            {
                int index = FielderIndexOf(director, role);
                return index == chaser || index == holder;
            }
            bool rightSide = director.GetBattedBallSnapshot().SprayAngleDegrees > 0f;
            FielderRole secondCover = rightSide ? FielderRole.Shortstop : FielderRole.SecondBase;
            if (Handling(secondCover)) secondCover = rightSide ? FielderRole.SecondBase : FielderRole.Shortstop;
            bool firstBaseHandling = Handling(FielderRole.FirstBase);
            for (int i = 0; i < director.FielderCount; i++)
            {
                FielderController fielder = director.GetFielder(i);
                Vector3 goal = fielder.Position;
                if (holder == i)
                {
                    if (TryChooseTarget(director, out BaseId target))
                    {
                        Vector3 basePosition = field.GetBasePosition(target);
                        if (Flat(basePosition - fielder.Position).magnitude < 12f) goal = basePosition;
                        else director.RequestFielderThrow(i, ToThrowTarget(target));
                    }
                }
                else if (i == chaser) goal = chase;
                else if (fielder.Role == FielderRole.Catcher) goal = field.HomePosition;
                else if (fielder.Role == FielderRole.FirstBase) goal = field.FirstBasePosition;
                else if (fielder.Role == FielderRole.Pitcher && firstBaseHandling) goal = field.FirstBasePosition;
                else if (fielder.Role == secondCover && (toSecond || director.IsRunnerForced(1))) goal = field.SecondBasePosition;
                else if (fielder.Role == FielderRole.ThirdBase && (toThird || director.IsRunnerForced(2))) goal = field.ThirdBasePosition;
                Vector3 toGoal = Flat(goal - fielder.Position);
                float gap = toGoal.magnitude;
                director.RequestFielderMove(i, gap < 0.1f ? Vector2.zero : new Vector2(toGoal.x, toGoal.z) / gap * Mathf.Clamp01(gap / 1.5f));
            }
        }

        private static bool TryChooseTarget(PlayDirector director, out BaseId target)
        {
            target = BaseId.First;
            for (int slot = director.RunnerSlotCount - 1; slot >= 0; slot--)
            {
                RunnerSnapshot r = director.GetRunnerSnapshot(slot);
                int forcedBase = (int)r.StartBase + 1;
                if (r.IsLive && director.IsRunnerForced(slot) && r.Phase != RunnerPhase.Holding && forcedBase <= 3)
                {
                    target = (BaseId)forcedBase;
                    return true;
                }
            }
            for (int slot = 1; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot r = director.GetRunnerSnapshot(slot);
                if (r.IsLive && director.RunnerNeedsRetouch(slot))
                {
                    target = r.StartBase;
                    return true;
                }
            }
            for (int slot = 0; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot r = director.GetRunnerSnapshot(slot);
                if (r.IsLive && r.Phase != RunnerPhase.Holding)
                {
                    target = r.NextBase;
                    return true;
                }
            }
            return false;
        }

        /// <summary>뜬공은 진공 탄도 낙하 지점, 굴러가는 공은 0.6초 뒤 위치를 쫓는다.</summary>
        private static Vector3 PredictChasePoint(Vector3 position, Vector3 velocity)
        {
            float g = Physics.gravity.magnitude;
            if (position.y > 0.3f)
            {
                float t = (velocity.y + Mathf.Sqrt(velocity.y * velocity.y + 2f * g * position.y)) / g;
                return position + new Vector3(velocity.x, 0f, velocity.z) * t;
            }
            return position + new Vector3(velocity.x, 0f, velocity.z) * 0.6f;
        }

        private static ThrowTarget ToThrowTarget(BaseId baseId)
        {
            switch (baseId)
            {
                case BaseId.First: return ThrowTarget.First;
                case BaseId.Second: return ThrowTarget.Second;
                case BaseId.Third: return ThrowTarget.Third;
                default: return ThrowTarget.Home;
            }
        }

        private static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);

        private static void RequireOutcomeRewards(TrainingEnvController controller)
        {
            PlaySummary play = controller.LastPlaySummary;
            Require(Mathf.Abs(controller.LastDefenseOutcomeReward - PlayOutcomeRewards.Defense(play)) < 1e-5f, "defense outcome reward matches the rule");
            Require(Mathf.Abs(controller.LastRunnerOutcomeReward - PlayOutcomeRewards.Runners(play)) < 1e-5f, "runner group outcome reward matches the rule");
            Require(Mathf.Abs(controller.LastBatterOutcomeReward - PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.InPlay, true, play)) < 1e-5f,
                "batter plate-appearance outcome matches the rule");
            // 시나리오 타구는 타자 결정 없이 시작하므로 그 타석은 에피소드로 닫히지 않는다(결정이 있던 타석만 누적 보상을 비교한다).
            BatterAgent batter = controller.Batter;
            if (batter.LastPlateAppearanceHadDecisions)
                Require(Mathf.Abs(batter.LastCompletedCumulativeReward - (batter.LastPlateAppearanceAddedReward + batter.LastOutcomeReward)) < 1e-4f,
                    "batter episode reward = pitch rewards + outcome");
        }

        /// <summary>
        /// 수비 보조 보상 합 = Σ(γΦ(k+1) − Φ(k)) − Φ(마지막) = −Φ(0) + (γ − 1)ΣΦ(k≥1). 끝난 상태의 포텐셜을 0으로 두었는지,
        /// 결정 시점마다 한 번씩만 줬는지를 확인한다.
        /// </summary>
        private static void RequireDefenseShaping(TrainingEnvController controller, List<float> potentials)
        {
            Require(potentials.Count > 0, "fielder decision potentials sampled");
            float expected = -potentials[0];
            for (int k = 1; k < potentials.Count; k++) expected += (PlayOutcomeRewards.DefenseShapingGamma - 1f) * potentials[k];
            Require(Mathf.Abs(controller.LastDefenseShapingReward - expected) < 1e-4f,
                $"defense shaping sum = -Φ0 + (γ-1)ΣΦ (got {controller.LastDefenseShapingReward:F5}, expected {expected:F5})");
        }

        /// <summary>
        /// 역할 임무를 규칙 표대로 독립 계산한다. 쫓는 수비수도 예상 지점으로 따로 구한다. 임무가 없으면(쫓기·공 소유·송구 후) false다.
        /// 포수 홈, 1루수 1루, 3루수 3루(반경 3 m 베이스 커버). 투수는 1루수가 공을 처리하면 1루, 아니면 시작 위치 6 m.
        /// 2루수·유격수는 분사각 ≤ 0이면 2루수, > 0이면 유격수가 2루(먼저인 쪽이 처리 중이면 다른 쪽), 나머지는 시작 위치 6 m. 외야수는 12 m.
        /// </summary>
        private static bool ExpectedDuty(PlayDirector director, int f, out Vector3 anchor, out float radius, out bool coversBase)
        {
            anchor = Vector3.zero;
            radius = 0f;
            coversBase = false;
            int chaser = director.BattedBallFielded ? -1 : NearestFielder(director, ExpectedChasePoint(director), out _);
            bool Busy(int i) => i == chaser || director.BallHolder == i || (director.BallHolder < 0 && director.LastThrower == i);
            int IndexOf(FielderRole role)
            {
                for (int i = 0; i < director.FielderCount; i++)
                    if (director.GetFielder(i).Role == role) return i;
                return -1;
            }
            if (Busy(f)) return false;
            FieldLayout field = director.FieldLayout;
            FielderController body = director.GetFielder(f);
            BaseId? baseDuty = null;
            switch (body.Role)
            {
                case FielderRole.Catcher: baseDuty = BaseId.Home; break;
                case FielderRole.FirstBase: baseDuty = BaseId.First; break;
                case FielderRole.ThirdBase: baseDuty = BaseId.Third; break;
                case FielderRole.Pitcher: if (Busy(IndexOf(FielderRole.FirstBase))) baseDuty = BaseId.First; break;
                case FielderRole.SecondBase:
                case FielderRole.Shortstop:
                    Vector3 exit = director.GetBattedBallSnapshot().ExitVelocity;
                    bool rightSide = Mathf.Atan2(exit.x, exit.z) > 0f;
                    FielderRole preferred = rightSide ? FielderRole.Shortstop : FielderRole.SecondBase;
                    FielderRole other = rightSide ? FielderRole.SecondBase : FielderRole.Shortstop;
                    FielderRole coverer = Busy(IndexOf(preferred)) && !Busy(IndexOf(other)) ? other : preferred;
                    if (coverer == body.Role) baseDuty = BaseId.Second;
                    break;
            }
            if (baseDuty.HasValue)
            {
                anchor = field.GetBasePosition(baseDuty.Value);
                radius = 3f;
                coversBase = true;
                return true;
            }
            anchor = body.HomeSpot;
            radius = body.Role == FielderRole.LeftField || body.Role == FielderRole.CenterField || body.Role == FielderRole.RightField ? 12f : 6f;
            return true;
        }

        /// <summary>결정 시점의 베이스 커버 개인 포텐셜 Φ_i를 모으며 역할 표와 비교한다. 베이스 커버 임무만 −계수 × 베이스 거리이고 나머지는 0이다.</summary>
        private static float[] SampleCoverPotentials(PlayDirector director)
        {
            var sample = new float[director.FielderCount];
            for (int f = 0; f < sample.Length; f++)
            {
                sample[f] = PlayOutcomeRewards.CoverPotential(director, f);
                FielderController body = director.GetFielder(f);
                float expected = ExpectedDuty(director, f, out Vector3 anchor, out _, out bool coversBase) && coversBase
                    ? -PlayOutcomeRewards.CoverPerMeter * Flat(body.Position - anchor).magnitude : 0f;
                Require(Mathf.Abs(sample[f] - expected) < 1e-5f,
                    $"{body.name} cover potential follows the role table (got {sample[f]:F5}, expected {expected:F5})");
            }
            return sample;
        }

        /// <summary>베이스 커버 개인 보조 보상 합도 수비수마다 −Φ_i(0) + (γ − 1)ΣΦ_i(k≥1)와 같아야 한다.</summary>
        private static void RequireCoverShaping(TrainingEnvController controller, List<float[]> cover)
        {
            Require(cover.Count > 0, "cover potentials sampled");
            for (int j = 0; j < controller.Fielders.Length; j++)
            {
                List<float> column = Column(cover, controller.Fielders[j].FielderIndex);
                float expected = -column[0];
                for (int k = 1; k < column.Count; k++) expected += (PlayOutcomeRewards.DefenseShapingGamma - 1f) * column[k];
                float got = controller.GetLastCoverShapingReward(j);
                Require(Mathf.Abs(got - expected) < 1e-4f,
                    $"{controller.Fielders[j].name} cover shaping sum = -Φ0 + (γ-1)ΣΦ (got {got:F5}, expected {expected:F5})");
            }
        }

        private static float[] SamplePositionRewards(PlayDirector director)
        {
            var result = new float[director.FielderCount];
            for (int f = 0; f < result.Length; f++)
            {
                FielderController body = director.GetFielder(f);
                float expected = ExpectedDuty(director, f, out Vector3 anchor, out float radius, out _)
                    ? -Mathf.Min(0.1f, 0.005f * Mathf.Max(0f, Flat(body.Position - anchor).magnitude - radius)) * Time.fixedDeltaTime : 0f;
                result[f] = PlayOutcomeRewards.PositionReward(director, f, Time.fixedDeltaTime);
                Require(Mathf.Abs(result[f] - expected) < 1e-6f && result[f] <= 0f,
                    $"{body.name} time-scaled position reward follows the role table (got {result[f]:F6}, expected {expected:F6})");
            }
            return result;
        }

        private static void RequirePositionRewards(TrainingEnvController controller, float[] expected)
        {
            float total = 0f;
            for (int j = 0; j < controller.Fielders.Length; j++)
            {
                float value = expected[controller.Fielders[j].FielderIndex];
                Require(Mathf.Abs(controller.GetLastPositionReward(j) - value) < 1e-5f,
                    $"{controller.Fielders[j].name} position sum survives terminal settlement without cancellation");
                total += value;
            }
            Require(Mathf.Abs(controller.LastPositionReward - total) < 1e-5f, "position total matches individual sums");
            RequireStat(controller, "Defense Reward/Position", total);
        }

        /// <summary>개인 포구 보상은 타구에 처음 닿은 수비수 한 명만, 플레이당 한 번 받는다. 끝에서 회수하지 않는다.</summary>
        private static void RequireFieldingRewards(TrainingEnvController controller, float[] expected)
        {
            float total = 0f;
            for (int j = 0; j < controller.Fielders.Length; j++)
            {
                float value = expected[controller.Fielders[j].FielderIndex];
                Require(Mathf.Abs(controller.GetLastFieldingReward(j) - value) < 1e-6f,
                    $"{controller.Fielders[j].name} fielding reward goes only to the first fielder");
                total += value;
            }
            Require(Mathf.Abs(controller.LastFieldingReward - total) < 1e-6f &&
                (total == 0f || Mathf.Abs(total - PlayOutcomeRewards.FieldingReward) < 1e-6f), "at most one fielding reward per play");
            // 플레이가 끝나면 Director가 초기화되므로, 종료 시점에 기록한 포구 지표와 비교한다.
            RequireStat(controller, "Defense/Fielded", total > 0f ? 1f : 0f);
            RequireStat(controller, "Defense Reward/Fielding", total);
        }

        /// <summary>
        /// 역할 임무와 자리 이탈 감점을 정지 상태에서 확인한다. 쫓는 수비수를 고정하려고 보조 수비수(중견수, 중견수 검사 때는 좌익수)를
        /// 예상 지점 위에 둔다. 3루 쪽(분사각 0)과 1루 쪽(분사각 > 0) 타구에서 각각 확인하고, 2루 커버 역할 교대와
        /// 1루수가 공을 쫓을 때 투수의 1루 커버도 본다.
        /// </summary>
        private static void VerifyPositionDuties(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            int center = FielderIndexOf(director, FielderRole.CenterField), left = FielderIndexOf(director, FielderRole.LeftField);
            int firstBase = FielderIndexOf(director, FielderRole.FirstBase), pitcherIndex = FielderIndexOf(director, FielderRole.Pitcher);
            int second = FielderIndexOf(director, FielderRole.SecondBase), shortstop = FielderIndexOf(director, FielderRole.Shortstop);
            foreach (bool rightSide in new[] { false, true })
            {
                SetSituation(controller, new Situation(false, false, false, 0));
                director.RequestScriptedBattedBall(new Vector3(rightSide ? 1f : 0f, 0f, 3f));
                BatterAgentVerification.Advance(director);
                Require(director.State == PlayState.BattedBallInFlight, "position duty test starts a live ball");
                var positions = new Vector3[director.FielderCount];
                for (int f = 0; f < positions.Length; f++) positions[f] = director.GetFielder(f).Position;
                Vector3 chasePoint = ExpectedChasePoint(director);
                Vector3 onChase = new Vector3(chasePoint.x, 0f, chasePoint.z);
                Vector3 far = onChase + Vector3.forward * 150f;
                void Restore()
                {
                    for (int k = 0; k < positions.Length; k++) director.GetFielder(k).transform.position = positions[k];
                }
                try
                {
                    for (int f = 0; f < positions.Length; f++)
                    {
                        int helper = f == center ? left : center;
                        Restore();
                        director.GetFielder(helper).transform.position = onChase;
                        FielderController body = director.GetFielder(f);
                        bool expectedDuty = ExpectedDuty(director, f, out Vector3 anchor, out float radius, out bool coversBase);
                        bool hasDuty = PlayOutcomeRewards.TryGetPositionDuty(director, f, out PositionDuty duty);
                        Require(expectedDuty && hasDuty, $"{body.name} has a duty while another fielder chases");
                        Require(Flat(duty.Anchor - anchor).magnitude < 1e-4f && Mathf.Approximately(duty.Radius, radius) && duty.CoversBase == coversBase,
                            $"{body.name} duty matches the role table (got {duty.Anchor} r{duty.Radius}, expected {anchor} r{radius})");
                        foreach (float extra in new[] { -radius, -0.1f, 0f, 0.1f, 1f, 100f })
                        {
                            float distance = radius + extra;
                            body.transform.position = anchor + Vector3.right * distance;
                            Require(PlayOutcomeRewards.DefenseChaser(director) == helper, "position distance test keeps the helper as the chaser");
                            float expected = -Mathf.Min(0.1f, 0.005f * Mathf.Max(0f, distance - radius));
                            float got = PlayOutcomeRewards.PositionReward(director, f, 1f);
                            Require(Mathf.Abs(got - expected) < 1e-6f, $"{body.name} position radius and cap at {distance:F1} m (got {got:F5}, expected {expected:F5})");
                            Require(Mathf.Abs(PlayOutcomeRewards.PositionReward(director, f, 0.5f) - 0.5f * got) < 1e-6f, "position time scaling");
                            float cover = coversBase ? -PlayOutcomeRewards.CoverPerMeter * distance : 0f;
                            Require(Mathf.Abs(PlayOutcomeRewards.CoverPotential(director, f) - cover) < 1e-5f, $"{body.name} cover potential only for base duties");
                        }
                        Require(PlayOutcomeRewards.PositionReward(director, f, float.NaN) == 0f &&
                            PlayOutcomeRewards.PositionReward(director, f, float.PositiveInfinity) == 0f &&
                            PlayOutcomeRewards.PositionReward(director, f, -1f) == 0f, "invalid elapsed time is ignored");
                        director.GetFielder(helper).transform.position = far;
                        body.transform.position = onChase;
                        Require(PlayOutcomeRewards.DefenseChaser(director) == f && !PlayOutcomeRewards.TryGetPositionDuty(director, f, out _) &&
                            PlayOutcomeRewards.PositionReward(director, f, 1f) == 0f && PlayOutcomeRewards.CoverPotential(director, f) == 0f,
                            $"{body.name} is exempt from its duty while chasing");
                    }
                    // 1루수가 공을 쫓으면 투수가 1루를 맡는다.
                    Restore();
                    director.GetFielder(firstBase).transform.position = onChase;
                    Require(PlayOutcomeRewards.DefenseChaser(director) == firstBase &&
                        PlayOutcomeRewards.TryGetPositionDuty(director, pitcherIndex, out PositionDuty pitcherDuty) &&
                        pitcherDuty.CoversBase && pitcherDuty.Base == BaseId.First, "pitcher covers first while the first baseman chases");
                    // 2루 커버: 3루 쪽 타구는 2루수, 1루 쪽은 유격수가 먼저다. 그 수비수가 쫓으면 다른 쪽이 맡는다.
                    int coverer = rightSide ? shortstop : second, backup = rightSide ? second : shortstop;
                    Restore();
                    director.GetFielder(center).transform.position = onChase;
                    Require(PlayOutcomeRewards.TryGetPositionDuty(director, coverer, out PositionDuty first) && first.CoversBase && first.Base == BaseId.Second &&
                        PlayOutcomeRewards.TryGetPositionDuty(director, backup, out PositionDuty idle) && !idle.CoversBase,
                        $"{director.GetFielder(coverer).name} covers second on a {(rightSide ? "first" : "third")}-base-side ball");
                    director.GetFielder(center).transform.position = far;
                    director.GetFielder(coverer).transform.position = onChase;
                    Require(PlayOutcomeRewards.DefenseChaser(director) == coverer &&
                        PlayOutcomeRewards.TryGetPositionDuty(director, backup, out PositionDuty swap) && swap.CoversBase && swap.Base == BaseId.Second,
                        $"{director.GetFielder(backup).name} covers second while {director.GetFielder(coverer).name} chases");
                }
                finally
                {
                    Restore();
                    director.RequestResetPlay();
                    BatterAgentVerification.Advance(director);
                }
                for (int f = 0; f < positions.Length; f++)
                    Require(PlayOutcomeRewards.PositionReward(director, f, 1f) == 0f, "inactive play has no position penalty");
            }
        }

        /// <summary>쫓을 지점을 독립 계산한다. 떠 있는 공은 지금 속도·중력의 진공 낙하 지점, 굴러가는 공은 지금 위치다.</summary>
        private static Vector3 ExpectedChasePoint(PlayDirector director)
        {
            PitchSnapshot ball = director.GetSnapshot();
            Vector3 point = ball.BallPosition;
            float g = -Physics.gravity.y;
            float height = point.y - director.FieldLayout.HomePosition.y - director.EnvironmentConfig.BallRadius;
            if (height > 0f && g > 0f)
            {
                float vy = ball.BallVelocity.y;
                float time = (vy + Mathf.Sqrt(vy * vy + 2f * g * height)) / g;
                point += new Vector3(ball.BallVelocity.x, 0f, ball.BallVelocity.z) * time;
            }
            return point;
        }

        private static int NearestFielder(PlayDirector director, Vector3 point, out float distance)
        {
            int nearest = -1;
            distance = float.PositiveInfinity;
            for (int f = 0; f < director.FielderCount; f++)
            {
                float d = Flat(director.GetFielder(f).Position - point).magnitude;
                if (d >= distance) continue;
                nearest = f;
                distance = d;
            }
            return nearest;
        }

        /// <summary>쫓기 포텐셜을 독립 계산한다. 첫 포구 전 한 명만 거리 계수, 포구 뒤에는 전원 0이다.</summary>
        private static float[] SampleChasePotentials(PlayDirector director)
        {
            int nearest = NearestFielder(director, ExpectedChasePoint(director), out float distance);
            var sample = new float[director.FielderCount];
            for (int f = 0; f < sample.Length; f++)
            {
                sample[f] = PlayOutcomeRewards.ChasePotential(director, f);
                float expected = !director.BattedBallFielded && f == nearest ? -PlayOutcomeRewards.DefenseChasePerMeter * distance : 0f;
                Require(Mathf.Abs(sample[f] - expected) < 1e-5f, $"chase potential belongs only to the nearest fielder ({f})");
            }
            Require(PlayOutcomeRewards.DefenseChaser(director) == (director.BattedBallFielded ? -1 : nearest),
                "chase reward and duty exemption use the same chaser");
            return sample;
        }

        /// <summary>그룹 포텐셜에 쫓기 성분이 중복되지 않는지 독립 계산으로 확인한다.</summary>
        private static float SampleGroupPotential(PlayDirector director)
        {
            float expected = director.BattedBallFielded ? PlayOutcomeRewards.DefenseFieldedValue : 0f;
            for (int slot = 0; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot runner = director.GetRunnerSnapshot(slot);
                if (!runner.IsLive || !director.IsRunnerForced(slot)) continue;
                BaseId forced = (BaseId)(((int)runner.StartBase + 1) % 4);
                Vector3 point = director.FieldLayout.GetBasePosition(forced);
                float nearest = float.PositiveInfinity;
                for (int f = 0; f < director.FielderCount; f++)
                    nearest = Mathf.Min(nearest, Flat(director.GetFielder(f).Position - point).magnitude);
                expected -= PlayOutcomeRewards.DefenseCoverPerMeter * nearest;
            }
            float got = PlayOutcomeRewards.DefensePotential(director);
            Require(Mathf.Abs(got - expected) < 1e-5f, "group potential contains fielding and force cover only");
            return got;
        }

        private static void RequireChaseShaping(TrainingEnvController controller, List<float[]> chase)
        {
            Require(chase.Count > 0, "chase potentials sampled");
            float total = 0f;
            for (int j = 0; j < controller.Fielders.Length; j++)
            {
                List<float> column = Column(chase, controller.Fielders[j].FielderIndex);
                float expected = -column[0];
                for (int k = 1; k < column.Count; k++) expected += (PlayOutcomeRewards.DefenseShapingGamma - 1f) * column[k];
                float got = controller.GetLastChaseShapingReward(j);
                Require(Mathf.Abs(got - expected) < 1e-4f,
                    $"{controller.Fielders[j].name} chase shaping sum = -Φ0 + (γ-1)ΣΦ (got {got:F5}, expected {expected:F5})");
                total += got;
            }
            Require(Mathf.Abs(controller.LastChaseShapingReward - total) < 1e-4f, "chase shaping total matches individual rewards");
            RequireStat(controller, "Defense Reward/Chase Shaping", total);
        }

        private static List<float> SumSamples(List<float[]> samples)
        {
            var sums = new List<float>(samples.Count);
            foreach (float[] sample in samples)
            {
                float sum = 0f;
                foreach (float value in sample) sum += value;
                sums.Add(sum);
            }
            return sums;
        }

        private static void VerifyFielderMovement(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            BatterAgentVerification.FinishCurrentPlateAppearance(controller);
            Vector2[] inputs = { new Vector2(4f, 2f), new Vector2(-2f, 4f), new Vector2(-4f, -2f),
                new Vector2(2f, -4f), new Vector2(0.3f, 0.4f), new Vector2(0.06f, 0.08f), Vector2.zero,
                new Vector2(float.NaN, 1f), new Vector2(1f, float.PositiveInfinity) };
            foreach (Vector2 input in inputs)
            {
                foreach (FielderAgent agent in controller.Fielders)
                {
                    director.GetFielder(agent.FielderIndex).ResetState(director.FieldLayout.HomePosition);
                    agent.OnActionReceived(new ActionBuffers(new[] { input.x, input.y }, new[] { 0 }));
                }
                // Ready의 명령 적용만 진행하고 각 몸을 직접 적분해 Academy 중립 행동에 덮이지 않게 한다.
                BatterAgentVerification.Advance(director);
                // 정책 출력에 MoveActionGain(3)을 곱한 뒤 크기 1로 제한한다. (0.3, 0.4)는 최고 속력, (0.06, 0.08)은 0.3배 속력이다.
                Vector2 expected = BaseballEnvironmentConfig.IsFinite(input.x) && BaseballEnvironmentConfig.IsFinite(input.y)
                    ? Vector2.ClampMagnitude(input * FielderAgent.MoveActionGain, 1f) : Vector2.zero;
                foreach (FielderAgent agent in controller.Fielders)
                {
                    FielderController body = director.GetFielder(agent.FielderIndex);
                    for (int step = 0; step < 25; step++) body.Tick(Time.fixedDeltaTime, director.EnvironmentConfig, director.FieldLayout.HomePosition);
                    Vector3 velocity = new Vector3(expected.x, 0f, expected.y) * director.EnvironmentConfig.FielderSpeed;
                    Require(Vector3.Distance(body.Velocity, velocity) < 1e-4f, $"{agent.name} preserves input direction, applies the move gain and caps speed for {input}");
                    body.ResetState(director.FieldLayout.HomePosition);
                    Require(body.Position == body.HomeSpot && body.Velocity == Vector3.zero, "fielder reset clears position and velocity");
                    director.RequestFielderMove(agent.FielderIndex, Vector2.zero);
                }
            }
            BatterAgentVerification.Advance(director);
        }

        private static List<float> Column(List<float[]> samples, int index)
        {
            var column = new List<float>(samples.Count);
            foreach (float[] sample in samples) column.Add(sample[index]);
            return column;
        }

        private static int FielderIndexOf(PlayDirector director, FielderRole role)
        {
            for (int i = 0; i < director.FielderCount; i++)
                if (director.GetFielder(i).Role == role) return i;
            throw new Exception("Training stage verification failed: no fielder with role " + role);
        }

        private static readonly FielderRole[] CoverRoles =
            { FielderRole.Catcher, FielderRole.FirstBase, FielderRole.SecondBase, FielderRole.ThirdBase, FielderRole.Shortstop, FielderRole.Pitcher };

        private static string DescribeCoverSums(TrainingEnvController controller)
        {
            var text = new StringBuilder();
            foreach (FielderRole role in CoverRoles)
            {
                int j = Array.FindIndex(controller.Fielders, agent => controller.Director.GetFielder(agent.FielderIndex).Role == role);
                if (text.Length > 0) text.Append(", ");
                text.Append($"{role} {controller.GetLastCoverShapingReward(j):+0.000;-0.000}");
            }
            return text.ToString();
        }

        private static string DescribeCover(PlayDirector director, float[] sample)
        {
            var text = new StringBuilder();
            foreach (FielderRole role in CoverRoles)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append($"{role} {sample[FielderIndexOf(director, role)]:+0.000;-0.000}");
            }
            return text.ToString();
        }

        /// <summary>처음 <paramref name="transitions"/>개 결정 사이의 보조 보상 합 Σ(γΦ(k+1) − Φ(k)).</summary>
        private static float ShapingSum(List<float> potentials, int transitions)
        {
            float sum = 0f;
            for (int k = 0; k < transitions; k++) sum += PlayOutcomeRewards.DefenseShapingGamma * potentials[k + 1] - potentials[k];
            return sum;
        }

        private static float Stat(TrainingEnvController controller, string key)
        {
            Require(controller.Stats.TryGetLast(key, out float value), $"TensorBoard stat '{key}' recorded");
            return value;
        }

        private static void RequireStat(TrainingEnvController controller, string key, float expected)
        {
            float value = Stat(controller, key);
            Require(Mathf.Abs(value - expected) < 1e-4f, $"TensorBoard stat '{key}' = {expected} (got {value})");
        }

        private static void RequireNoStat(TrainingEnvController controller, string key) =>
            Require(!controller.Stats.TryGetLast(key, out _), $"TensorBoard stat '{key}' is not recorded in this stage");

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("Training stage verification failed: " + message);
        }
    }
}
