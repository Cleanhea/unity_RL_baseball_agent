namespace BaseballSimulation
{
    /// <summary>타석이 끝난 방식. 인플레이 타구의 세부 결과는 <see cref="PlaySummary"/>에 있다.</summary>
    public enum PlateAppearanceResult
    {
        None = 0,
        Strikeout = 1,
        Walk = 2,
        InPlay = 3,
    }

    /// <summary>볼카운트·아웃·주자 상황 읽기 전용 값(docs/game-situation.md).</summary>
    public readonly struct SituationSnapshot
    {
        public SituationSnapshot(int balls, int strikes, int outs, bool onFirst, bool onSecond, bool onThird,
            int runsThisHalfInning, bool plateAppearanceOver, PlateAppearanceResult result, bool halfInningOver)
        {
            Balls = balls; Strikes = strikes; Outs = outs; OnFirst = onFirst; OnSecond = onSecond; OnThird = onThird;
            RunsThisHalfInning = runsThisHalfInning; PlateAppearanceOver = plateAppearanceOver; Result = result;
            HalfInningOver = halfInningOver;
        }
        public int Balls { get; }
        public int Strikes { get; }
        /// <summary>이번 반 이닝 아웃 수(0~3). 3이면 반 이닝이 끝났다.</summary>
        public int Outs { get; }
        public bool OnFirst { get; }
        public bool OnSecond { get; }
        public bool OnThird { get; }
        public int RunsThisHalfInning { get; }
        public bool PlateAppearanceOver { get; }
        public PlateAppearanceResult Result { get; }
        public bool HalfInningOver { get; }
        public bool IsOccupied(BaseId baseId) =>
            baseId == BaseId.First ? OnFirst : baseId == BaseId.Second ? OnSecond : baseId == BaseId.Third && OnThird;
    }

    /// <summary>한 인플레이 플레이의 결과 요약. 보상 계산의 근거다.</summary>
    public readonly struct PlaySummary
    {
        public PlaySummary(bool resolved, PitchEndReason endReason, int outs, int runs, int batterBases, int runnerOuts, int basesAdvanced)
        {
            Resolved = resolved; EndReason = endReason; Outs = outs; Runs = runs; BatterBases = batterBases;
            RunnerOuts = runnerOuts; BasesAdvanced = basesAdvanced;
        }
        /// <summary>플레이가 판정까지 진행됐으면 true다. 1·2단계처럼 판정 전에 초기화하면 false다.</summary>
        public bool Resolved { get; }
        public PitchEndReason EndReason { get; }
        /// <summary>이 플레이에서 나온 아웃 수(타자 뜬공 아웃 포함).</summary>
        public int Outs { get; }
        /// <summary>인정된 득점. 세 번째 아웃이 포스 아웃이거나 타자주자가 1루 전에 아웃되면 그 플레이의 득점은 없다.</summary>
        public int Runs { get; }
        /// <summary>타자주자가 안전하게 얻은 베이스 수(0~4). 아웃이면 0이다.</summary>
        public int BatterBases { get; }
        /// <summary>베이스 위 주자 아웃(포스·태그·리터치 전 태그). 타자의 뜬공 아웃은 뺀다.</summary>
        public int RunnerOuts { get; }
        /// <summary>살아남은 주자와 타자주자가 이 플레이에서 전진한 베이스 수의 합(득점은 홈까지).</summary>
        public int BasesAdvanced { get; }
    }

    /// <summary>
    /// 볼카운트·아웃·베이스 점유·득점을 관리하는 규칙 계산기(docs/game-situation.md). ML-Agents·Unity 물리에 의존하지 않고,
    /// <see cref="PlayDirector"/>가 투구 판정과 플레이 결과를 넘겨준다. 한 타석이 끝나면 다음 타석 시작에 카운트를 지우고,
    /// 세 번째 아웃이 나오면 다음 타석 시작에 아웃·주자·반 이닝 득점을 지운다.
    /// </summary>
    public sealed class GameSituation
    {
        private readonly bool[] bases = new bool[3];

        public int Balls { get; private set; }
        public int Strikes { get; private set; }
        public int Outs { get; private set; }
        public int RunsThisHalfInning { get; private set; }
        public bool PlateAppearanceOver { get; private set; }
        public PlateAppearanceResult Result { get; private set; }
        public bool HalfInningOver => Outs >= 3;
        /// <summary>
        /// 누상 주자를 다음 타석으로 넘길지. 주자 오브젝트가 없는 씬(수동 조작·1·2단계)은 false라
        /// 볼넷·안타가 있어도 베이스가 비어 있다(카운트·아웃은 그대로 센다).
        /// </summary>
        public bool TrackBases { get; set; } = true;

        public bool IsOccupied(int baseIndex) => baseIndex >= 1 && baseIndex <= 3 && bases[baseIndex - 1];

        public SituationSnapshot GetSnapshot() => new SituationSnapshot(Balls, Strikes, Outs, bases[0], bases[1], bases[2],
            RunsThisHalfInning, PlateAppearanceOver, Result, HalfInningOver);

        /// <summary>끝난 타석이 있으면 새 타석을 연다. 반 이닝이 끝났으면 아웃·주자·득점도 지운다.</summary>
        public void BeginPlateAppearanceIfNeeded()
        {
            if (!PlateAppearanceOver && !HalfInningOver) return;
            if (HalfInningOver)
            {
                Outs = 0;
                RunsThisHalfInning = 0;
                System.Array.Clear(bases, 0, bases.Length);
            }
            Balls = Strikes = 0;
            PlateAppearanceOver = false;
            Result = PlateAppearanceResult.None;
        }

        /// <summary>다음 타석을 새로 시작하도록 표시한다(카운트만 지운다).</summary>
        public void EndPlateAppearance()
        {
            PlateAppearanceOver = true;
        }

        /// <summary>새 반 이닝 상황을 정한다(검증·학습 상황 다양화용). 카운트와 반 이닝 득점을 지운다.</summary>
        public void SetSituation(bool onFirst, bool onSecond, bool onThird, int outs)
        {
            bases[0] = TrackBases && onFirst;
            bases[1] = TrackBases && onSecond;
            bases[2] = TrackBases && onThird;
            Outs = System.Math.Max(0, System.Math.Min(2, outs));
            RunsThisHalfInning = 0;
            Balls = Strikes = 0;
            PlateAppearanceOver = false;
            Result = PlateAppearanceResult.None;
        }

        /// <summary>투구 판정으로 카운트를 바꾼다. 볼넷·삼진은 여기서 타석을 끝내고, 인플레이는 타석만 끝낸다(주자는 플레이 결과로 정한다).</summary>
        public void RecordPitchCall(PitchCall call)
        {
            if (PlateAppearanceOver || HalfInningOver) return;
            switch (call)
            {
                case PitchCall.Ball:
                    if (++Balls >= 4) Walk();
                    break;
                case PitchCall.CalledStrike:
                case PitchCall.SwingingStrike:
                    if (++Strikes >= 3)
                    {
                        Outs++;
                        Finish(PlateAppearanceResult.Strikeout);
                    }
                    break;
                case PitchCall.Foul:
                    if (Strikes < 2) Strikes++;
                    break;
                case PitchCall.InPlay:
                    Finish(PlateAppearanceResult.InPlay);
                    break;
            }
        }

        /// <summary>판정까지 끝난 인플레이 플레이의 새 베이스 점유·아웃·득점을 반영한다.</summary>
        public void ApplyPlayResult(bool onFirst, bool onSecond, bool onThird, int outs, int runs)
        {
            Outs = System.Math.Min(3, Outs + outs);
            RunsThisHalfInning += runs;
            bases[0] = TrackBases && onFirst;
            bases[1] = TrackBases && onSecond;
            bases[2] = TrackBases && onThird;
        }

        /// <summary>볼넷: 타자는 1루, 밀려나는 주자만 한 베이스씩. 만루면 1점.</summary>
        private void Walk()
        {
            if (TrackBases)
            {
                if (!bases[0]) bases[0] = true;
                else if (!bases[1]) bases[1] = true;
                else if (!bases[2]) bases[2] = true;
                else RunsThisHalfInning++;
            }
            Finish(PlateAppearanceResult.Walk);
        }

        private void Finish(PlateAppearanceResult result)
        {
            PlateAppearanceOver = true;
            Result = result;
        }
    }
}
