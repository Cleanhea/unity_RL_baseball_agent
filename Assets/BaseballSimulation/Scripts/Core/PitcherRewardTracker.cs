using System;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>한 투구의 투수 보상 성분(docs/pitcher-reward-design.md).</summary>
    public readonly struct PitcherRewardSnapshot
    {
        public PitcherRewardSnapshot(bool active, bool complete, float strike, float ball, float contact,
            float exitSpeed, float popFly, float foul, float homeRun)
        {
            Active = active; Complete = complete; Strike = strike; Ball = ball; Contact = contact;
            ExitSpeed = exitSpeed; PopFly = popFly; Foul = foul; HomeRun = homeRun;
        }

        public bool Active { get; }
        public bool Complete { get; }
        public float Strike { get; }
        public float Ball { get; }
        public float Contact { get; }
        public float ExitSpeed { get; }
        public float PopFly { get; }
        public float Foul { get; }
        public float HomeRun { get; }
        public float Total => Strike + Ball + Contact + ExitSpeed + PopFly + Foul + HomeRun;
    }

    /// <summary>
    /// 한 투구의 투수 보상 계산기. 타자 보상(<see cref="BatterRewardTracker"/>)과 같은 환경 사건을 구독하고
    /// 같은 시점에 끝나며, 타자에게 좋은 사건은 대체로 반대 부호로 준다. 볼넷·삼진의 큰 값은 타석이 끝날 때
    /// <see cref="PlayOutcomeRewards.PitcherPlateAppearance"/>가 주고, 여기서는 투구 단위의 작은 값만 준다.
    /// ML-Agents 타입에 의존하지 않는다.
    /// </summary>
    public sealed class PitcherRewardTracker : IDisposable
    {
        public const float StrikeReward = 0.5f;
        public const float BallPenalty = -0.1f;
        public const float ContactPenalty = -0.5f;
        public const float FoulReward = 0.4f;
        public const float HomeRunPenalty = -3f;

        private readonly PlayDirector director;
        private bool disposed;
        private bool active;
        private bool complete;
        private bool contactPaid;
        private bool speedPaid;
        private bool popFlyPaid;
        private bool foulPaid;
        private bool homeRunPaid;
        private bool callPaid;
        private float strike;
        private float ball;
        private float contact;
        private float exitSpeed;
        private float popFly;
        private float foul;
        private float homeRun;

        public event Action<float> RewardAdded;
        public event Action<PitcherRewardSnapshot> EpisodeCompleted;

        public PitcherRewardTracker() { }

        public PitcherRewardTracker(PlayDirector source)
        {
            director = source != null ? source : throw new ArgumentNullException(nameof(source));
            director.BallBatContact += OnContact;
            director.BattedBallCalled += OnBattedBallCalled;
            director.PitchCalled += OnPitchCalled;
        }

        public PitcherRewardSnapshot GetSnapshot() =>
            new PitcherRewardSnapshot(active, complete, strike, ball, contact, exitSpeed, popFly, foul, homeRun);

        public void BeginEpisode()
        {
            if (disposed) throw new ObjectDisposedException(nameof(PitcherRewardTracker));
            active = true;
            complete = contactPaid = speedPaid = popFlyPaid = foulPaid = homeRunPaid = callPaid = false;
            strike = ball = contact = exitSpeed = popFly = foul = homeRun = 0f;
        }

        public float RecordContact()
        {
            if (!active || complete || contactPaid) return 0f;
            contactPaid = true;
            contact = ContactPenalty;
            RewardAdded?.Invoke(contact);
            return contact;
        }

        public float RecordPitchCall(PitchCall call, bool hasContact)
        {
            if (!active || complete || call == PitchCall.None) return 0f;
            float delta = 0f;
            if (!hasContact && !contactPaid && !callPaid)
            {
                if (call == PitchCall.CalledStrike || call == PitchCall.SwingingStrike) strike = delta = StrikeReward;
                else if (call == PitchCall.Ball) ball = delta = BallPenalty;
                callPaid = delta != 0f;
                if (delta != 0f) RewardAdded?.Invoke(delta);
            }
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
                        exitSpeed = -BatterRewardTracker.SpeedBonus(hit.ExitSpeed);
                        delta += exitSpeed;
                        if (exitSpeed != 0f) RewardAdded?.Invoke(exitSpeed);
                    }
                    if (hit.Call == BattedBallCall.Fair && hit.HasFirstTouch && !popFlyPaid)
                    {
                        popFlyPaid = true;
                        popFly = -BatterRewardTracker.PopFlyPenalty(hit.ApexHeight, hit.HangTime);
                        delta += popFly;
                        if (popFly != 0f) RewardAdded?.Invoke(popFly);
                    }
                    if (hit.Call == BattedBallCall.HomeRun && !homeRunPaid)
                    {
                        homeRunPaid = true;
                        homeRun = HomeRunPenalty;
                        delta += homeRun;
                        RewardAdded?.Invoke(homeRun);
                    }
                    Finish();
                    break;

                case BattedBallCall.Foul:
                    if (!foulPaid)
                    {
                        foulPaid = true;
                        foul = FoulReward;
                        delta += foul;
                        RewardAdded?.Invoke(foul);
                    }
                    Finish();
                    break;

                case BattedBallCall.OutOfPlay:
                    Finish();
                    break;
            }
            return delta;
        }

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
    }
}
