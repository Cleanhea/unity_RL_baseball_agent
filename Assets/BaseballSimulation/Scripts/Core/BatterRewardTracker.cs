using System;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>한 투구의 타자 보상 성분. 환경의 경기 결과나 타자 평가 점수는 변경하지 않는다.</summary>
    public readonly struct BatterRewardSnapshot
    {
        public BatterRewardSnapshot(bool active, bool complete, float contact, float miss,
            float exitSpeed, float popFly, float foul, float homeRun)
        {
            Active = active;
            Complete = complete;
            Contact = contact;
            Miss = miss;
            ExitSpeed = exitSpeed;
            PopFly = popFly;
            Foul = foul;
            HomeRun = homeRun;
        }

        public bool Active { get; }
        public bool Complete { get; }
        public float Contact { get; }
        public float Miss { get; }
        public float ExitSpeed { get; }
        public float PopFly { get; }
        public float Foul { get; }
        public float HomeRun { get; }
        public float Total => Contact + Miss + ExitSpeed + PopFly + Foul + HomeRun;
    }

    /// <summary>
    /// docs/batter-reward-design.md의 한 투구 보상 계산기. PlayDirector 생성자는 환경 이벤트를
    /// 구독하고, 매 투구 전에 BeginEpisode를 호출한다. 매개변수 없는 생성자와 Record 메서드는
    /// 동일한 계산을 제어된 시나리오에서 검사할 때 쓴다. ML-Agents 타입에 의존하지 않는다.
    /// </summary>
    public sealed class BatterRewardTracker : IDisposable
    {
        public const float ContactReward = 0.5f;
        public const float MissPenalty = -1f;
        public const float FoulPenalty = -1.5f;
        public const float HomeRunReward = 3f;
        public const float SpeedFloor = 25f;
        public const float SpeedRange = 25f;
        public const float PopFlyHeightFloor = 15f;
        public const float PopFlyHeightRange = 15f;
        public const float PopFlyTimeFloor = 3f;
        public const float PopFlyTimeRange = 2f;

        private readonly PlayDirector director;
        private bool disposed;
        private bool active;
        private bool complete;
        private bool contactPaid;
        private bool speedPaid;
        private bool popFlyPaid;
        private bool foulPaid;
        private bool homeRunPaid;
        private bool missPaid;
        private float contact;
        private float miss;
        private float exitSpeed;
        private float popFly;
        private float foul;
        private float homeRun;

        /// <summary>각 보상 성분이 확정될 때 그 증분을 발행한다. Agent는 AddReward(delta)에 연결할 수 있다.</summary>
        public event Action<float> RewardAdded;
        /// <summary>이 투구의 타자 보상이 확정되면 한 번 발행한다.</summary>
        public event Action<BatterRewardSnapshot> EpisodeCompleted;

        public BatterRewardTracker() { }

        public BatterRewardTracker(PlayDirector source)
        {
            director = source != null ? source : throw new ArgumentNullException(nameof(source));
            director.BallBatContact += OnContact;
            director.BattedBallCalled += OnBattedBallCalled;
            director.PitchCalled += OnPitchCalled;
        }

        public BatterRewardSnapshot GetSnapshot() => new BatterRewardSnapshot(active, complete,
            contact, miss, exitSpeed, popFly, foul, homeRun);

        /// <summary>투구 요청 전에 호출한다. 이전 투구의 합계와 중복 지급 플래그를 모두 지운다.</summary>
        public void BeginEpisode()
        {
            if (disposed) throw new ObjectDisposedException(nameof(BatterRewardTracker));
            active = true;
            complete = contactPaid = speedPaid = popFlyPaid = foulPaid = homeRunPaid = missPaid = false;
            contact = miss = exitSpeed = popFly = foul = homeRun = 0f;
        }

        public float RecordContact()
        {
            if (!active || complete || contactPaid) return 0f;
            contactPaid = true;
            contact = ContactReward;
            RewardAdded?.Invoke(contact);
            return contact;
        }

        public float RecordPitchCall(PitchCall call, bool hasContact)
        {
            if (!active || complete || call == PitchCall.None) return 0f;
            float delta = 0f;
            if (!hasContact && !contactPaid && !missPaid &&
                (call == PitchCall.CalledStrike || call == PitchCall.SwingingStrike))
            {
                missPaid = true;
                miss = MissPenalty;
                delta = miss;
                RewardAdded?.Invoke(delta);
            }
            // InPlay may mean an unresolved batted ball timed out; it is not proof of a fair hit.
            Finish();
            return delta;
        }

        public float RecordBattedBallCall(BattedBallSnapshot hit)
        {
            if (!active || complete || !contactPaid) return 0f;
            float delta = 0f;
            switch (hit.Call)
            {
                case BattedBallCall.Fair:
                case BattedBallCall.HomeRun:
                case BattedBallCall.GroundRuleDouble:
                    if (!speedPaid)
                    {
                        speedPaid = true;
                        exitSpeed = SpeedBonus(hit.ExitSpeed);
                        delta += exitSpeed;
                        if (exitSpeed != 0f) RewardAdded?.Invoke(exitSpeed);
                    }
                    if (hit.Call == BattedBallCall.Fair && hit.HasFirstTouch && !popFlyPaid)
                    {
                        popFlyPaid = true;
                        popFly = PopFlyPenalty(hit.ApexHeight, hit.HangTime);
                        delta += popFly;
                        if (popFly != 0f) RewardAdded?.Invoke(popFly);
                    }
                    if (hit.Call == BattedBallCall.HomeRun && !homeRunPaid)
                    {
                        homeRunPaid = true;
                        homeRun = HomeRunReward;
                        delta += homeRun;
                        RewardAdded?.Invoke(homeRun);
                    }
                    Finish();
                    break;

                case BattedBallCall.Foul:
                    if (!foulPaid)
                    {
                        foulPaid = true;
                        foul = FoulPenalty;
                        delta += foul;
                        RewardAdded?.Invoke(foul);
                    }
                    Finish();
                    break;

                case BattedBallCall.OutOfPlay:
                    // A missing fence is an invalid training setup; it earns no fair-hit bonus.
                    Finish();
                    break;
            }
            return delta;
        }

        public static float SpeedBonus(float metresPerSecond) =>
            IsFinite(metresPerSecond) ? Mathf.Clamp01((metresPerSecond - SpeedFloor) / SpeedRange) : 0f;

        public static float PopFlyPenalty(float apexHeight, float hangTime) =>
            IsFinite(apexHeight) && IsFinite(hangTime)
                ? -Mathf.Clamp01((apexHeight - PopFlyHeightFloor) / PopFlyHeightRange) *
                  Mathf.Clamp01((hangTime - PopFlyTimeFloor) / PopFlyTimeRange)
                : 0f;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            active = false;
            if (director == null) return;
            director.BallBatContact -= OnContact;
            director.BattedBallCalled -= OnBattedBallCalled;
            director.PitchCalled -= OnPitchCalled;
        }

        private void OnContact(Vector3 _) => RecordContact();
        private void OnBattedBallCalled(BattedBallCall _) => RecordBattedBallCall(director.GetBattedBallSnapshot());
        private void OnPitchCalled(PitchCall call) => RecordPitchCall(call, director.HasContact);

        private void Finish()
        {
            if (complete) return;
            complete = true;
            EpisodeCompleted?.Invoke(GetSnapshot());
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
