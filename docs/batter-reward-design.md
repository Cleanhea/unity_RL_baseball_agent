# 타자 강화학습 보상 설계 초안

## 타이밍 집중·정타 진행 보상 강화 (2026-10-09)

Camera30 타자는 배트 위치·각도를 고정한 타이밍 과정부터 높이·각도·배트 위치를 차례로 열고 전체 준비로 연결한다. 몸 고정·30장·100Hz와 최종 정타·성적 통과 기준은 유지한다. 준비 과정 및 `batter_prepared=1`의 정타 미달 실제 페어 보상 상한을 0.5→1.5로 높이고 접촉 품질 75%·속도 25%로 진행 보상을 강화한다. [최신 계약·수식·실행](batter-timing-progress.md)을 따른다. 아래 약한 페어 0~0.5 기록은 변경 전 설명이며 준비 모드가 아닌 기존 학습에는 계속 적용한다.

## 2단계 타자 준비 과정 (2026-10-08)

준비 과정과 batter_prepared=1 모드는 실제 페어 타구의 정타 보상을 단계2에도 유지한다. 스윙 시도·기다리기에 추가 보상은 없고, 약한 페어0~0.5/정타≥2.63, 파울·삼진·볼넷과 기존 정산 경계는 유지한다. 기존 YAML 보상은 바꾸지 않는다. [계약·실행·검증](batter-preparation.md)을 따른다.

## 1단계 접촉 훈련 보상 (2026-10-08)

`BatterRewardTracker`의 기본 여섯 성분은 유지한다. `BatterAgent`가 1단계에만 아래 보정을 적용하며 에피소드는 한 타석이다. 2·3단계와 파라미터 없는 실행은 기본 투구 보상과 타석 결과 보상을 쓴다. 아래의 투구 단위 에피소드·YAML 미구현 설명은 2026-09-24 당시 기록이다.

- 과정 0: 실제 접촉에 +1.5를 한 번 더 준다. 완료 판정이 파울·장외·미확정이면 추가분을 회수해 파울의 순보상은 기본 +0.5−1.5=−1이다. 미리 지급한 추가분의 회수도 투구·타석·Agent 합계에 한 번 반영한다.
- 과정 1·2·3: 실제 페어 접촉의 **전체 투구 보상**을 접촉 품질 Q, 타구 속도 v(km/h), 발사각 a(°)로 계산한다. Q≥0.6, v≥120, 5≤a≤35이면 `2 + 0.5×Q + 0.5×clip01(v/180)`이다(2.63~3). 홈런은 기본 합계 `0.5 + 3 + SpeedBonus(v/3.6)`를 유지하며 정타로 센다.
- 그 밖의 실제 페어 접촉은 `0.5 × (0.5×clip01(Q/0.6) + 0.5×clip01(v/120)) × L(a)`다. `L(a)=clip01(a/5)×clip01((50−a)/15)`로 5~35°에서는 1, 음의 각도와 50° 이상에서는 0이다. 약한 타구 보상은 최대 0.5이므로 이후 전환 기준 >1을 단독으로 통과할 수 없다. 볼넷만으로도 >1을 넘지 못한다.

정타는 **실제 접촉과 유효 페어 판정**이 있어야 한다. 타이밍 점수·거리·스크립트 타구만으로 보상을 만들지 않는다. 질적 목표를 만족하지 못해도 작은 연속 보상으로 정확도·속도·각도를 탐색할 수 있다. 물리의 최소/최대 속도, 파워 난수, 배트 접촉, 야구 판정은 유지한다.

`LastPitchReward`는 기본 여섯 성분, `LastPitchTrainingContactBonus`는 회수 후 추가 접촉분, `LastPitchTrainingQualityReward`는 **부호가 있는 보정분**이다. `LastPitchAddedReward = 기본 합 + 추가 접촉분 + 품질 보정분`이며 타석·Agent 합계에도 한 번 들어간다. 예를 들어 61 km/h·−22°의 페어 타구는 과정 1 이후 기본 +0.5를 보정 −0.5로 상쇄해 0을 받는다. 미접촉 감점·파울 감점·타석 결과 보상은 별도로 남는다.

TensorBoard에는 `Batter Reward/Training Contact Bonus`, `Batter Reward/Training Quality Adjustment`, `Batted Ball/Contact Quality`, `Batter Training/Lesson`, `Batter Training/Qualified Hit`를 기록한다. 마지막 지표는 완료한 모든 훈련 타석에 정타 1/기타 0을 넣는다. 실제 하드히트 분류(152.9 km/h)와는 다른 훈련 목표다. 새 네 과정 학습의 장시간 효과는 별도 실행에서 확인해야 한다.

2026-09-24 사용자 요청의 여섯 조건을 **한 투구당 한 타자 에피소드**의 보상으로 정의했다. 다음은 당시 제안값이며 현재 기본 계산기로 유지한다. 보상은 환경 판정에 영향을 주지 않는다.

## 학습 범위와 기본 조건

- 에이전트는 기존 `RequestBatterSetup(BatterSetupCommand)`으로 투구 전 타자·손잡이 위치를 정하고, 투구 중 `RequestSwing(SwingCommand)`을 한 번 요청하거나 스윙하지 않는다. 주루 판단과 수비는 이 보상에 포함하지 않는다.
- 첫 실험은 `PitchLocationMode.MachineTarget`, 고정 스윙 파워 배율 `(1.5, 1.5)`, 자동 반복 투구 꺼짐으로 시작한다. 현재는 투구 도중 손잡이를 옮길 수 없어 무작위 존 끝 투구를 기준 자세에서 칠 수 없는 경우가 있다. 초기 보상 결과를 먼저 확인한 뒤 투구 위치와 파워를 무작위로 넓힌다.
- 공이 존 밖으로 들어왔고 스윙하지 않아 `PitchCall.Ball`이면 보상 0이다. 맞힐 필요가 없는 볼을 쫓아 스윙하도록 강요하지 않는다. 존 안의 공을 지켜보거나 스윙 후 헛치면 아래 미접촉 감점을 준다.

## 제안 보상식

한 에피소드의 총보상은 아래 **사건별 보상 합계**다. `clip01(x) = min(1, max(0, x))`이며, m/s·m·s 단위를 사용한다.

| 사용자 조건 | 지급 시점과 조건 | 제안값 |
| --- | --- | ---: |
| 1. 공에 맞춤 | `BallBatContact`가 한 번 발생 | `+0.5` |
| 2. 공을 못 맞춤 | 접촉 없이 투구가 `CalledStrike` 또는 `SwingingStrike`로 끝남 | `-1.0` |
| 3. 공이 빠르게 날아감 | 첫 페어 판정(`Fair`, `HomeRun`, `GroundRuleDouble`) 때 실제 타구 초기 속도 `v` 사용 | `+1.0 × clip01((v - 25) / 25)` |
| 4. 높게 오래 뜸 | 첫 `Fair` 판정 때 홈런이 아니고 첫 닿음이 확인된 경우. 최고 높이 `h`, 체공 시간 `t` 사용 | `-1.0 × clip01((h - 15) / 15) × clip01((t - 3) / 2)` |
| 5. 파울 | `BattedBallCall.Foul` 확정 시 한 번 | `-1.5` |
| 6. 홈런 | `BattedBallCall.HomeRun` 확정 시 한 번 | `+3.0` |

속도 보상은 **페어 타구에만** 준다. 빠른 파울로 접촉 보상과 속도 보상을 반복해서 얻는 전략을 막기 위해서다. 파울의 순보상은 접촉 `+0.5`와 파울 `-1.5`를 합친 `-1.0`이다. `Fair` 이후 `GroundRuleDouble`로 판정이 바뀌어도 속도 보상을 다시 주지 않는다. 인정 2루타의 별도 보너스는 이번 여섯 조건에 없으므로 넣지 않는다.

뜬공 감점은 높이와 시간 **둘 다** 기준을 넘을 때 커진다. 최고 높이 15 m 이하 또는 체공 시간 3 s 이하면 0이다. 홈런은 높은 비행이 필요할 수 있으므로 이 감점에서 제외하고 홈런 보너스를 지급한다. 파울은 이미 감점하므로 뜬공 감점을 겹쳐 주지 않는다. `BattedBallSnapshot.ApexHeight`는 현재 홈 높이 0 m를 전제로 한 월드 높이이고, `HangTime`은 접촉부터 첫 지면/펜스 닿음까지다.

| 예시 결과 | 계산 | 총보상 |
| --- | --- | ---: |
| 헛스윙 또는 존 안 공을 지켜봄 | 미접촉 `-1.0` | `-1.0` |
| 존 밖 볼을 지켜봄 | 보상 사건 없음 | `0.0` |
| 약한 페어 타구, 25 m/s, 낮은 궤적 | 접촉 `+0.5` | `+0.5` |
| 빠른 페어 타구, 40 m/s, 낮은 궤적 | `+0.5 + (40-25)/25` | `+1.1` |
| 높은 페어 뜬공, 25 m/s, 최고 30 m, 체공 5 s | `+0.5 - 1.0` | `-0.5` |
| 빠른 파울 타구, 50 m/s | `+0.5 - 1.5`, 속도 보상 없음 | `-1.0` |
| 홈런, 50 m/s | `+0.5 + 1.0 + 3.0`, 뜬공 감점 없음 | `+4.5` |

## 현재 환경 신호와 지급 규칙

| 보상 사건 | 현재 환경에서 읽을 신호 |
| --- | --- |
| 접촉 | `PlayDirector.BallBatContact` 또는 `GetBattingEvaluation().HasContact` |
| 미접촉 | `PlayDirector.PitchCalled`의 `CalledStrike`/`SwingingStrike`와 `HasContact == false` |
| 속도 | `PlayDirector.BattedBallCalled`의 첫 페어 판정, `GetBattedBallSnapshot().ExitSpeed` |
| 높은 뜬공 | 첫 `Fair` 판정의 `HasFirstTouch`, `ApexHeight`, `HangTime` |
| 파울·홈런 | `PlayDirector.BattedBallCalled`의 `Foul`/`HomeRun` |

한 고정 단계에서 `BattedBallCalled`와 `PitchCalled`가 함께 올 수 있으므로 에피소드별 `contactPaid`, `speedPaid`, `popFlyPaid`, `foulPaid`, `homeRunPaid`, `missPaid` 플래그로 각 항목을 최대 한 번만 지급한다. `PitchCall.InPlay`는 타구가 아직 페어/파울로 확정되지 않은 시간 초과에도 나올 수 있으므로 페어 속도 보상의 근거로 쓰지 않는다. 첫 `Fair` 판정 이후에는 속도·뜬공 보상을 확정하고, 뒤늦은 `GroundRuleDouble`은 중복 지급하지 않는다. 검증용 씬에는 충돌 가능한 외야 펜스가 있어야 하며, 펜스가 없는 비정상 `OutOfPlay`는 이 보상 실험에서 제외한다.

타자 에피소드는 미접촉 `PitchCalled` 또는 첫 `Fair`/`Foul`/`HomeRun`/`GroundRuleDouble` 타구 판정에서 끝낸다. 첫 `Fair` 뒤 나중에 일어날 수 있는 `GroundRuleDouble`은 이번 여섯 조건의 보상을 바꾸지 않으므로 기다리지 않는다. 사건이 확정되지 않은 채 시간 초과되면 이미 지급한 접촉 보상만 유지하고 종료한다. `GetBattingEvaluation().Scores` 네 값은 진단용으로 기록하되 이번 보상에 더하지 않는다.

## 코드 연결

`BatterRewardTracker(PlayDirector)`가 `BallBatContact`, `BattedBallCalled`, `PitchCalled`를 구독한다. `BatterAgent`는 `PlayReset` 직후 `BeginEpisode()`를 호출하고, `RewardAdded(float)`의 증분을 `AddReward`에 전달하며, `EpisodeCompleted(BatterRewardSnapshot)`에서 `EndEpisode`를 호출한다. `GetSnapshot()`은 여섯 보상 성분·총합과 활성/완료 상태를 제공한다. 사용을 마치면 `Dispose()`로 이벤트 구독을 해제한다. 기본 생성자와 `RecordContact`/`RecordBattedBallCall`/`RecordPitchCall`은 제어된 검증용으로도 사용할 수 있다.

```csharp
rewards = new BatterRewardTracker(director);
rewards.RewardAdded += AddReward;
rewards.EpisodeCompleted += snapshot => EndEpisode();
// OnEpisodeBegin에서 director.RequestResetPlay();
// PlayReset 처리기에서 rewards.BeginEpisode();
```

## 검증 기준

1. 위 예시 일곱 개의 총보상과 사건별 지급 횟수를 자동 검사한다. 특히 빠른 파울의 속도 보상 0, 홈런의 뜬공 감점 0을 확인한다. 환경 이벤트 스트림 전체를 별도로 흘려 `Fair → GroundRuleDouble`에서도 속도 보상이 한 번만 계산되는지 확인한다.
2. 고정 시드와 같은 명령 순서에서 보상 성분·합계가 반복 재현되는지 확인한다. 씬/설정 초기화 뒤 이전 에피소드의 지급 플래그가 남지 않아야 한다.
3. 학습 시 접촉률, 헛스윙률, 파울률, 페어 타구 평균 초기 속도, 높은 장타가 아닌 뜬공 비율, 홈런률과 사건별 평균 보상을 따로 기록한다. 총보상만 높고 파울 또는 불필요한 스윙이 늘면 가중치를 조정한다.

타자 Agent와 관측/행동 공간은 `batter-agent.md`에 정의했다. 학습 YAML과 학습 실행은 아직 없다. 학습 단계에서 보상 성분별 기록을 유지하며 고정 조건의 반복 시나리오를 먼저 실행한다.
