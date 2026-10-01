# 학습 단계(커리큘럼)와 씬 구성

2026-09-26 사용자 요청으로 강화학습을 세 단계로 진행한다. 이 요청은 AGENTS.md의 "셀프플레이·다중 에이전트 학습·학습 YAML은 범위 밖" 제한을 사용자가 명시적으로 확장한 것이다. 각 단계는 따로 학습하며, 앞 단계에서 학습한 타자 모델을 다음 단계의 초기값으로 이어 쓴다. 2026-09-27에는 별도 실행 스크립트로 단계 전환을 자동화했다.

**자동 전환 기준:** 각 단계의 모든 Behavior가 해당 YAML의 `max_steps`에 도달하고 최종 모델·체크포인트가 저장되면 다음 단계 학습기를 시작한다. 이전 단계의 체크포인트를 `init_path`로 읽는다. 보상 점수나 승률로 조기 전환하지 않는다. `Training/auto_curriculum.py`는 각 씬의 독립 실행 파일을 별도 복사본에서 빌드해 차례로 실행한다. 실행 방법과 기존 1단계 학습에 붙는 방법은 [Training/README.md](../Training/README.md)에 있다.

| 단계 | Behavior별 학습 한도(`max_steps`) | 셀프플레이 설정(`--self-play`) |
| --- | --- | --- |
| 1 | 타자 300만 | 같음(공유) |
| 2 | 타자 600만, 투수 40만 | 타자 600만, 투수 26만 |
| 3 | 타자 600만, 투수 40만, 주자 200만, 수비 2,000만 | 타자 600만, 투수 26만, 주자 200만, 수비 2,000만 |

2026-09-27에 [고정 상대 평가](#고정-상대-평가-2026-09-27)와 [셀프플레이 설정](#동시-학습과-셀프플레이)을 더했다.

| 단계 | 씬 | 학습 Agent (Behavior) | 투구 |
| --- | --- | --- | --- |
| 1 타자 | `Scenes/Training/Stage1_Batter.unity` | 타자 `BaseballBatter` | 스크립트 투수가 항상 존 중앙으로 직구를 던진다. 구속만 바뀐다 |
| 2 타자+투수 | `Scenes/Training/Stage2_BatterPitcher.unity` | 타자, 투수 `BaseballPitcher` | 투수 Agent가 구종·구속·위치를 고른다 |
| 3 전체 | `Scenes/Training/Stage3_FullTeam.unity` | 타자, 주자 4명 `BaseballRunner`(타자주자 + 1·2·3루), 투수, 수비 5명 `BaseballFielder` | 투수 Agent |

3단계 수비는 1루수·유격수·3루수·좌중간·우중간 외야수 5명이다. 포수와 2루수는 없다. 구종은 포심·투심·커브·슬라이더·체인지업이다.

## 공통 구조

**씬 만들기.** `BaseballPlayground`는 수동 조작·검증용 틀이다. `Tools > Baseball Simulation > Training > Build Stage N Scene` 메뉴가 다음 순서로 단계 씬을 만든다.

1. 틀 씬에 남은 학습 Agent를 지워 수동 조작 씬으로 되돌린다.
2. 틀 씬을 `Scenes/Training/`으로 복사한다. 같은 단계를 다시 만들면 씬 GUID는 유지하고 내용만 새로 쓴다.
3. 단계에 필요한 Agent와 `TrainingEnvController`를 더한다.

틀 씬을 고친 뒤 메뉴를 다시 실행하면 단계 씬에 반영된다. 단계 씬을 직접 고친 내용은 다시 만들 때 사라진다.

**병렬 경기장 (2026-09-27).** 단계 씬에는 경기장이 여러 개 들어간다. 기본은 4개이고, `Tools > Baseball Simulation > Training > Arena Count > 1 / 4 / 8` 중 체크된 값으로 만든다.

- **배치:** 빌더가 경기장 하나를 완성한 뒤 `BaseballEnvironment` 루트를 X 방향으로 400 m 간격으로 복제한다. 회전 없이 옮기기만 한다.
- **참조:** 복제본 안의 참조(Director·Agent·컨트롤러)는 복제본끼리 다시 연결된다.
- **난수:** `TrainingEnvController.arenaIndex`로 스크립트 투수·상황·투구 위치·스윙 파워 난수 시드를 경기장마다 다르게 한다. 0번 경기장은 단일 경기장과 같은 시드다. 나머지 경기장은 시드를 해시로 섞는다. 시드를 일정 간격으로만 바꾸면 `System.Random`의 첫 값들이 경기장끼리 거의 일직선으로 상관되기 때문이다.
- **화면:** 디버그 화면과 수동 입력은 0번 경기장에만 켜 둔다. 카메라는 0번을 비춘다.
- **좌표:** 경기장 위치와 무관하게 동작하도록 수비수 시작 위치는 부모 기준 로컬 좌표로 저장한다. 수비·주자 관측의 위치는 자기 경기장 홈 기준 좌표다. 그래서 같은 상황이면 어느 경기장에서나 관측이 같다(검증).
- **더 빠르게:** 경기장 여러 개인 씬을 빌드해 `--num-envs`로 여러 프로세스를 함께 띄울 수도 있다.

**결정 요청.** Agent는 `Decision Requester`를 쓰지 않는다. 각 씬의 `Systems/TrainingEnvController`가 `Academy.AgentPreStep`(Academy 한 단계 직전)에서 `PlayDirector` 상태를 보고, 지금 행동해야 하는 Agent에게만 `RequestDecision()`을 호출한다. 할 일이 없는 동안의 의미 없는 결정이 학습 자료에 섞이지 않게 하기 위해서다.

| 플레이 상태 | 결정을 요청하는 Agent |
| --- | --- |
| `Ready`, 타자 자세 전 | 타자 1회 (자세·손잡이) |
| `Ready`, 자세 후 | 1단계: 컨트롤러가 스크립트 투구를 요청. 2·3단계: 투수 1회 |
| `PitchInFlight` | 타자, 스윙할 때까지 매 고정 단계 |
| `BattedBallInFlight` (3단계) | 수비 5명과 살아 있는 주자 |

**플레이 종료와 초기화.**

- 투구가 끝나면 컨트롤러가 `RequestResetPlay()`를 요청한다. 1·2단계는 타자(와 투수)의 그 투구 보상이 확정되는 순간이다. 타구가 굴러가는 시간을 기다리지 않는다. 3단계는 플레이 판정이 끝날 때다.
- 볼카운트가 있어 **타자·투수 에피소드는 한 타석**이다. 볼넷·삼진·인플레이로 타석이 끝날 때 결과 보상을 주고 닫는다. 수비·주자 그룹 에피소드는 한 인플레이 플레이다([상황 규칙](game-situation.md)).
- 투구 요청이 거부되거나 한 플레이가 `maxPlaySteps`(기본 1500 고정 단계 = 30 s)를 넘으면, 열린 에피소드를 모두 `EpisodeInterrupted`로 닫고 초기화한다.
- 컨트롤러가 켜져 있는 동안 수동 입력과 자동 반복 투구는 꺼 둔다.

**모델 이어 쓰기.** 타자의 관측(벡터 16 + `BallEye` 레이 459)과 행동(연속 7 + 이산 [2])은 세 단계에서 같다. 그래서 1단계 체크포인트를 2·3단계의 `init_path`로 쓸 수 있다. 투수 계약도 2·3단계에서 같다.

**투구 명령.** 스크립트 투수와 투수 Agent 모두 `PlayDirector.RequestThrowPitch(PitchCommand)`를 쓴다. 명령에는 구종, 구속(m/s), 홈플레이트 목표 위치가 들어간다.

- 회전은 설정 에셋의 구종 프로필을 따른다: 구속 범위, 회전수, 휘는 방향, 회전 효율.
- `PitchPhysics.TrySolve`는 실제 비행과 같은 항력·마그누스 적분으로 좌우각·상하각을 함께 보정한다. 적분은 투구 시작점 기준 좌표에서 계산해 멀리 떨어진 병렬 경기장에서도 조준 오차의 반올림 누적을 줄인다. 그래서 휘는 공도 목표를 지난다.
- 기존 `RequestThrowPitch()`와 `RequestThrowPitch(Vector2)`는 수동 조작용 피칭머신(순수 역회전, 설정 구속) 동작을 그대로 유지한다.

## 1단계 — 타자

- **투구:** `TrainingEnvController`의 스크립트 투수가 매 투구 포심 직구를 **규칙 스트라이크존 중앙**으로 던진다. 중앙은 좌우 0, 존 아래·위 높이의 가운데(기본 0.755 m)이며 Scene 뷰의 존 테두리 중앙과 같다. 구속은 `scriptedSpeedRangeKmh`(기본 120~150 km/h)에서 투구마다 균등하게 뽑는다. 시드는 `scriptedPitchSeed`다.
- **존 중앙과 기준 자세:** 존 중앙은 피칭머신 목표점 `PitchTarget`(높이 1.0 m)보다 0.245 m 낮다. 타자 기준 자세·손잡이는 `PitchTarget` 기준이라, 중앙 공을 치려면 손잡이를 낮추는 자세를 학습해야 한다.
- **보상:** [타자 보상 설계](batter-reward-design.md) 그대로다.
- **학습 설정:** `Training/config/stage1_batter.yaml`
- **실행:** 저장소 루트에서 `mlagents-learn Training/config/stage1_batter.yaml --run-id=stage1_batter --results-dir=Training/results`를 실행한 뒤 Editor에서 `Stage1_Batter` 씬을 Play한다.

## 2단계 — 타자 + 투수

- **씬:** 1단계 씬 구성에 `Actors/Pitcher`(투수 Agent, 팀 1)를 더하고 피칭머신을 비활성화한다.
- **투구:** 타자가 자세를 잡으면 컨트롤러가 투수에게 한 번 결정을 요청한다. 투수는 구종 5종·구속·목표 위치를 고른다. 계약과 보상은 [투수 Agent](pitcher-agent.md)에 있다.
- **에피소드:** 타자와 투수 모두 한 투구가 한 에피소드이고 같은 판정에서 끝난다. 두 에피소드가 모두 닫히면 초기화한다.
- **학습 설정:** `Training/config/stage2_batter_pitcher.yaml`. 타자(팀 0)와 투수(팀 1)를 동시에 학습한다. 현재 YAML의 `self_play` 예시는 주석이므로 적용되지 않는다. 타자는 1단계 체크포인트(`init_path`)에서 시작한다.
- **실행:** 1단계 학습 결과가 `Training/results/stage1_batter`에 있어야 한다. `mlagents-learn Training/config/stage2_batter_pitcher.yaml --run-id=stage2_batter_pitcher --results-dir=Training/results` 후 `Stage2_BatterPitcher` 씬을 Play한다.

## 3단계 — 타자·주자 + 투수 + 수비 5명

- **씬:** 2단계 구성에 두 가지를 더한다.
  - 수비수 5명 `Actors/Fielder_1B|SS|3B|LCF|RCF`(`FielderController` + `FielderAgent`, 팀 1). `PlayDirector` 수비수 목록에도 같은 순서로 연결한다.
  - 누상 주자 3명 `Actors/BaseRunner_1B|2B|3B`(`RunnerController`). `PlayDirector` 누상 주자 슬롯에 연결한다.
  - 주자 4명(타자주자 + 누상 주자)의 `RunnerAgent`(팀 0).
- **시작 위치:** 1B (16, 23), SS (-8, 33), 3B (-17, 22.5), LCF (-24, 70), RCF (24, 70) m. 2루수가 없어 유격수가 가운데로 치우친다.
- **흐름:** 투구·타격은 2단계와 같다. 타구가 나오면 수비 5명과 살아 있는 주자가 0.1 s마다 결정한다. `PlayDirector`는 다음을 판정해 플레이를 끝낸다.
  - 포구·송구
  - 포스·태그·리터치 아웃과 세이프
  - 득점, 3아웃

  그러면 컨트롤러가 결과 보상을 주고 수비 그룹·주자 그룹 에피소드를 닫은 뒤 초기화한다. 주자 상황·아웃은 다음 타석으로 이어진다. 규칙·계약·보상은 [수비·주루 Agent](fielding-agents.md)와 [상황 규칙](game-situation.md)에 있다.
- **무작위 상황:** 새 타석의 30%(`randomSituationProbability`)는 누상 주자·아웃을 무작위로 정해 다양한 상황을 겪게 한다.
- **학습 설정:** `Training/config/stage3_full_team.yaml`
  - 타자·투수: PPO, 2단계 체크포인트에서 시작
  - 주자 그룹·수비 그룹: MA-POCA(`poca`)
  - 모든 behavior를 동시에 학습한다
- **실행:** `mlagents-learn Training/config/stage3_full_team.yaml --run-id=stage3_full_team --results-dir=Training/results` 후 `Stage3_FullTeam` 씬을 Play한다.

## TensorBoard 지표

2026-09-27 요청으로 보상 곡선 외에 야구 지표도 TensorBoard에서 보이게 했다. `TrainingEnvController`가 투구·타석·플레이가 끝날 때마다 `TrainingStats`로 값을 만들어 ML-Agents `StatsRecorder`로 보낸다. mlagents-learn은 Behavior의 `summary_freq`마다 값을 모아 기록한다.

- **집계 방식:** 기본은 평균이다. 0/1 값의 평균은 비율이다. 예를 들어 `Plate Appearance/Strikeout` 0.25는 삼진율 25%다.
- **히스토그램:** 표시한 값은 같은 이름의 평균 그래프와 `<이름>_hist` 분포 그래프가 함께 나온다.
- **Behavior별 기록:** 환경 지표는 그 환경의 모든 Behavior 로그에 같은 값으로 들어간다. mlagents 1.1.0 동작이다. 2·3단계는 `BaseballBatter` 쪽만 보면 된다. Behavior마다 `summary_freq`가 달라 점 간격만 다르다.
- **전송 조건:** 학습기에 연결됐을 때만 보낸다. 모델 없이 Play하거나 검증 메뉴를 실행해도 쌓이지 않는다.
- **영향 범위:** 보상·관측·행동은 바뀌지 않는다. 기존 체크포인트를 그대로 이어 쓸 수 있다.
- **빌드:** 이미 만든 `Training/builds/` 실행 파일에는 이 코드가 없다. 자동 실행 시 `--skip-build` 없이 다시 빌드해야 새 지표가 나온다.

| 그룹 / 이름 | 기록 시점·조건 | 단계 | 뜻 |
| --- | --- | --- | --- |
| `Pitch Call/Ball`·`Called Strike`·`Swinging Strike`·`Foul`·`In Play` | 판정이 난 투구마다 | 전체 | 투구 판정 비율 |
| `Plate Discipline/Zone Rate` | 판정이 난 투구마다 | 전체 | 존 통과 비율 |
| `Plate Discipline/Swing Rate` | 판정이 난 투구마다 | 전체 | 스윙 비율. 공이 플레이트를 지난 뒤 시작한 스윙은 넣지 않는다 |
| `Plate Discipline/Zone Swing Rate`·`Chase Rate` | 존 안·밖 투구마다 | 전체 | 존 안·밖 공에 대한 스윙 비율 |
| `Plate Discipline/Contact Rate` | 스윙마다 | 전체 | 컨택률. 1 − 값은 헛스윙률이다 |
| `Pitch/Speed (km/h)` | 투구마다 | 전체 | 실제 발사 구속 |
| `Pitch Type/Four Seam`·`Two Seam`·`Curve`·`Slider`·`Changeup` | 투구마다 | 2·3 | 투수 Agent의 구종 선택 비율 |
| `Batted Ball/Exit Speed (km/h)`·`Launch Angle (deg)`·`Spray Angle (deg)` (히스토그램) | 접촉마다 | 전체 | 타구 속도·발사각·방향각(+ 1루 쪽) |
| `Batted Ball/Fair`·`Home Run` | 페어/파울 판정이 난 타구마다 | 전체 | 페어(홈런·인정 2루타 포함) 비율, 홈런 비율 |
| `Batted Ball/Distance (m)` | 첫 닿음이 있는 페어 타구 | 전체 | 첫 바운드 지점 거리. 홈런은 펜스를 넘은 지점이다 |
| `Swing/Timing Error (ms)` (히스토그램) | 스윙마다 | 전체 | 목표 평면 도착 대비 타이밍. 음수는 이르고 양수는 늦다 |
| `Swing/Bat-Ball Distance (cm)` | 스윙마다(측정된 경우) | 전체 | 공과 배트의 최소 거리 |
| `Batter Reward/Contact`·`Miss`·`Exit Speed`·`Pop Fly`·`Foul`·`Home Run`·`Pitch Total` | 투구마다 | 전체 | [타자 보상](batter-reward-design.md) 성분별 투구당 평균 |
| `Batter Reward/Outcome` | 타석마다 | 전체 | 타석 결과 보상 |
| `Plate Appearance/Strikeout`·`Walk`·`In Play` | 타석마다 | 전체 | 삼진율·볼넷률·인플레이 비율 |
| `Plate Appearance/Pitches` | 타석마다 | 전체 | 타석당 투구 수 |
| `Plate Appearance/On Base` | 결과가 확정된 타석 | 3 | 출루율. 볼넷 또는 타자주자가 1루 이상 산 인플레이 |
| `Play/Outs`·`Runs`·`Batter Bases`·`Runner Outs`·`Bases Advanced` | 판정까지 끝난 인플레이 | 3 | 인플레이 한 번의 아웃·득점·타자 진루·주자 아웃·총 진루 |
| `Play/Live Time (s)` | 판정까지 끝난 인플레이 | 3 | 타구가 살아 있던 시간(수비 처리 속도) |
| `Play End/Fly Out`·`Force Out`·`Tag Out`·`Runner Safe`·`Run Scored`·`Home Run`·`Ground Rule Double`·`Timeout` | 판정까지 끝난 인플레이 | 3 | 플레이 종료 원인 비율 |
| `Half Inning/Runs` | 3아웃마다 | 3 | 반 이닝 득점. 무작위 상황으로 시작한 반 이닝은 상황을 정한 뒤의 득점이다 |
| `Defense/Fielded` | 수비가 결정한 플레이마다(파울·평가 타석 포함) | 3 | 수비가 타구를 한 번이라도 잡은 비율. 수비 학습이 시작됐는지 가장 먼저 보는 값이다 |
| `Defense Reward/Outcome`·`Shaping` | 수비가 결정한 플레이마다(파울·평가 타석 포함) | 3 | 수비 그룹의 결과 보상과 [보조 보상](fielding-agents.md#수비-보조-보상-2026-10-01) 합. `Environment/Group Cumulative Reward`에는 둘과 시간 감점이 모두 들어 있다. 보조 보상 합은 할인하지 않은 값이라 공·베이스에서 먼 상태가 길수록 오히려 커질 수 있다(검증: 제자리 수비 +1.01). 실력 지표로 쓰지 말고 수비 실력은 `Outcome`·`Fielded`·`Play/*`로 본다 |
| `Defense/Cover 1B`·`Cover 2B (SS)`·`Cover 3B` | 그 베이스로 진루·귀루 중인 주자가 있었던 수비 플레이마다 | 3 | 맡은 내야수(1루수·유격수·3루수)가 그 동안 베이스 반경 0.6 m 안에 들어간 비율. [내야 역할 보조 보상](fielding-agents.md#내야-역할-보조-보상-2026-10-01)이 통하는지 보는 값이다. 1루수는 거의 모든 땅볼에서 기록된다 |
| `Env/Aborted Play` | 초기화한 투구마다 | 전체 | 투구 거부·시간 초과로 중단한 비율. 0이 정상이다 |
| `Matchup/Batter Win`·`Pitcher Win`·`Draw` | Agent끼리 대결한 타석마다 | 2·3 | 타석 결과 보상(`Batter Reward/Outcome`)의 부호로 본 승패 비율. 셋의 합은 1이다 |
| `Benchmark Batter/…`·`Benchmark Pitcher/…` | 고정 상대 평가 타석 | 2·3 | [고정 상대 평가](#고정-상대-평가-2026-09-27) 참고 |

**해석 주의:**

- 1·2단계는 수비가 없다. 인플레이 타구는 판정 직후 초기화해 출루 여부를 모르므로 `On Base`와 `Play` 지표가 없다.
- 타격된 공은 플레이트를 지나지 않는다. 그래서 존 여부는 투구 목표 위치로 근사한다(실제 통과 오차는 3 cm 안).
- 수비가 공중에서 잡은 타구는 페어/파울 판정 없이 인플레이가 된다. 그래서 `Batted Ball/Fair` 비율에서 빠진다.
- 2단계부터 일반 지표(`Pitch Call`·`Plate Discipline`·`Pitch`·`Pitch Type`·`Batted Ball`·`Swing`·`Batter Reward`·`Plate Appearance`·`Play`·`Play End`·`Matchup`)는 **Agent끼리 대결한 타석만** 넣는다. 고정 상대 평가 타석은 `Benchmark` 묶음에만 들어간다. `Half Inning/Runs`, `Env/Aborted Play`, `Defense`·`Defense Reward`는 모든 타석을 넣는다. 평가 타석에서도 수비는 학습하기 때문이다.
- `Matchup`: 2단계는 삼진이 투수 승, 볼넷·홈런·인정 2루타가 타자 승이다. 판정 순간 플레이가 끝나 결과 보상이 생기기 때문이다. 그 밖의 페어 타구는 수비가 없어 결과 보상이 0이므로 무승부다. 3단계는 타자 진루·득점과 아웃으로 정해진다. 예를 들어 단타는 +0.5로 타자 승이고, 희생플라이(1아웃 1득점)는 0으로 무승부다.

## 고정 상대 평가 (2026-09-27)

동시 학습이나 셀프플레이에서는 상대도 계속 바뀐다. 그래서 보상 곡선이나 승패 비율만으로는 실제로 강해졌는지 알 수 없다. 2·3단계는 새 타석의 일부에서 한쪽을 고정한다. 그리고 같은 잣대로 잰 성적을 따로 기록한다. 경기장마다 `TrainingEnvController`가 새 타석을 시작할 때 타석 종류를 정한다.

| 타석 종류 (`PlateAppearanceOpponent`) | 기본 확률 | 고정되는 쪽 | 학습 데이터 | 지표 묶음 |
| --- | --- | --- | --- | --- |
| 기준 투수 `ScriptedPitcher` | `scriptedPitcherBenchmarkProbability` 0.1 | 투수: `BenchmarkPitcher` 스크립트 | 타자만 쓴다. 투수 Agent는 결정·보상·에피소드가 없다 | `Benchmark Batter/` |
| 고정 타자 `FrozenBatter` | `frozenBatterBenchmarkProbability` 0.1 | 타자: `Models/BenchmarkBatter_Stage1.onnx` 추론 | 투수만 쓴다. 타자 데이터는 학습기로 가지 않는다 | `Benchmark Pitcher/` |
| Agent끼리 `Live` | 나머지 0.8 | 없음 | 둘 다 | 일반 지표, `Matchup/` |

- **기준 투수:**
  - 구종 비율은 포심 35%, 슬라이더 23%, 투심 17%, 체인지업 13%, 커브 12%다. MLB 구종 비율을 이 환경의 5종으로 어림한 값이다. 커터·스플리터는 없고 스위퍼는 슬라이더로 합쳤다.
  - 구속은 구종 프로필 범위에서 균등하게 뽑는다.
  - 위치는 존 중앙을 중심으로 한 정규 분포다(좌우 σ 0.25 m, 높이 σ 0.28 m). 투수 Agent가 고를 수 있는 범위(플레이트·존 ± 0.25 m)로 자른다. 존 통과 비율은 약 절반이다(검증 표본 2만 개에서 50.8%).
  - 투수 Agent와 같은 명령 경계 `RequestThrowPitch(PitchCommand)`로 던진다.
- **고정 타자:**
  - 1단계 최종 모델(300만 스텝)을 복사한 것이다.
  - 타석 동안 타자의 Behavior Type을 `InferenceOnly`로, Model을 이 모델로 바꾼다. 타석이 끝나거나 중단되면 되돌린다.
  - 바꾸는 시점은 타자의 앞 타석 에피소드가 닫힌 뒤다. 그래서 학습기는 끊긴 에피소드를 보지 않는다.
  - 1단계 타자는 존 중앙 직구만 봤으므로 강한 타자가 아니다. 목적은 절대 실력이 아니라 **바뀌지 않는 기준**이다.
  - 다른 모델로 바꾸려면 파일을 덮어쓰거나 `TrainingSceneBuilder.BenchmarkBatterModelPath`를 바꾸고 단계 씬을 다시 만든다. 기준을 바꾸면 이전 실행과 지표를 비교할 수 없다. 모델 관측·행동 계약은 타자 Agent와 같아야 한다.
- **난수:** 타석 종류와 기준 투수는 `benchmarkSeed` 난수원을 쓴다. 경기장마다 시드를 섞는다. 타석마다 한 번씩 굴리므로 모델이 없어도 뒤 순서가 같다.
- **데이터 비용:** 타자는 고정 타자 타석(10%), 투수는 기준 투수 타석(10%)의 데이터를 잃는다. 반대로 기준 투수 타석은 타자에게 바뀌지 않는 상대를 섞어 주는 효과도 있다.
- **1단계:** 이미 스크립트 투수라 평가 타석이 없다.
- **학습기 없이 Play:** 고정 타자 타석에서는 모델이 친다. 나머지는 이전처럼 중립 행동이다.
- **확률 바꾸기:** 경기장마다 Inspector에서 바꾼다. 0이면 끈다. 단계 씬을 다시 만들면 기본값 0.1로 돌아간다.

지표(`<묶음>`은 `Benchmark Batter` 또는 `Benchmark Pitcher`):

| 이름 | 기록 시점 | 뜻 |
| --- | --- | --- |
| `<묶음>/Zone Rate` | 판정이 난 투구 | 존 통과 비율 |
| `<묶음>/Zone Swing Rate`·`Chase Rate` | 존 안·밖 투구 | 스윙 비율 |
| `<묶음>/Contact Rate` | 스윙 | 컨택률 |
| `<묶음>/Exit Speed (km/h)`·`Hard Hit` | 접촉 | 평균 타구 속도, 95 mph(152.9 km/h) 이상 비율 |
| `<묶음>/Home Run` | 페어/파울 판정이 난 타구 | 홈런 비율 |
| `<묶음>/Strikeout`·`Walk`·`In Play` | 타석 | 삼진·볼넷·인플레이 비율 |
| `<묶음>/Batter Outcome` | 타석 | 타자 타석 결과 보상 평균 |
| `<묶음>/On Base` | 결과가 확정된 타석(3단계) | 출루율 |

**읽는 법:**

- `Benchmark Batter`는 타자 관점이다. 컨택률·타구 속도·볼넷이 오르고 삼진·유인구 스윙(`Chase Rate`)이 내려가야 한다.
- `Benchmark Pitcher`는 투수 관점이다. 삼진·헛스윙이 오르고 타구 속도·출루율이 내려가야 한다.
- 경기장 4개·기본 확률이면 타자 `summary_freq`(2만 스텝) 한 구간에 평가 타석이 종류마다 약 40번뿐이다. 한 점은 크게 흔들리므로 TensorBoard smoothing으로 추세를 본다.

## 동시 학습과 셀프플레이

기본 설정은 사용자 요청대로 2·3단계의 참여 behavior를 **동시에** 학습한다. 공격과 수비가 서로에 맞춰 계속 바뀌어 학습이 흔들리면 셀프플레이 설정으로 바꾼다. 셀프플레이에서는 한 팀이 배우는 동안 상대 팀이 과거 스냅샷 중 하나로 고정되고, 정해진 스텝마다 번갈아 배운다.

- **흔들림 판단:** `Pitch Type/*`·`Matchup/*`이 오르내리기만 한다. 그런데 `Benchmark Batter/*`·`Benchmark Pitcher/*`는 나아지지 않는다. 그러면 서로 쫓아다니는 순환일 가능성이 크다.
- **실행:** `python Training/auto_curriculum.py --self-play`
  - 2·3단계는 `config/stage2_batter_pitcher_selfplay.yaml`·`stage3_full_team_selfplay.yaml`을 쓴다.
  - run-id는 `stage2_batter_pitcher_selfplay`·`stage3_full_team_selfplay`다. 동시 학습 결과와 섞이지 않는다.
  - 1단계 결과는 공유한다.
- **팀:** 타자·주자는 팀 0, 투수·수비는 팀 1이다. 씬 빌더가 정한다.
- **3단계는 타자·투수에만 셀프플레이:** ML-Agents 1.1.0은 셀프플레이 학습기마다 **자기 스텝 수로** 팀 교대를 요청하고, ELO는 팀당 학습기 하나만 등록한다(`mlagents/trainers/ghost/trainer.py`·`controller.py`). 한 팀에 셀프플레이 Behavior가 둘(타자+주자, 투수+수비)이면 교대가 불규칙해진다. 한 번에 두 번 뒤집혀 교대가 취소될 수도 있다. 그래서 주자·수비(MA-POCA)는 셀프플레이 없이 계속 배운다.
- **스텝 환산:** 학습기 스텝은 결정 수다. 2단계 학습 기록(12만 스텝)에서 타자는 타석당 51번 결정했다. 타석은 투구 2.2개였으므로 투구당 약 23번이다. 투수는 투구당 1번이다. 셀프플레이 값은 이 비율로 맞췄다.

| 값 | 타자 | 투수 | 뜻 |
| --- | --- | --- | --- |
| `team_change` | 200,000 | 8,700 | 한 차례에 약 8,700구씩 배운다 |
| `save_steps` | 50,000 | 2,200 | 배우는 동안 스냅샷 4개 |
| `swap_steps` | 50,000 | 2,200 | 상대 스냅샷이 한 차례에 4번 바뀐다 |
| `window` | 10 | 10 | 최근 스냅샷 10개에서 상대를 고른다 |
| `play_against_latest_model_ratio` | 0.5 | 0.5 | 절반은 최신 상대 |
| `max_steps` | 6,000,000 | 260,000 | 둘 다 약 259,000구를 배우고 함께 끝난다 |

- **시간:** 한 번에 한 팀만 배운다. 그래서 투구 수로는 동시 학습 2단계(투수 40만 구)의 약 1.3배가 든다.
  - 한쪽이 먼저 `max_steps`에 닿아도 ML-Agents는 교대를 계속한다. 그 팀 차례에는 업데이트 없이 시간이 흐른다.
  - 타자의 투구당 결정 수가 크게 바뀌면 값을 다시 맞춘다. 예를 들어 스윙을 훨씬 늦게 시작하게 되면 결정 수가 는다. 확인은 TensorBoard `Environment/Episode Length`(타석당 결정 − 1)를 `Plate Appearance/Pitches`로 나눈다.
- **`Self-play/ELO`는 믿지 않는다:**
  - ML-Agents ELO는 학습에 쓰이지 않는 표시용 값이다. 상대 스냅샷은 ELO와 무관하게 균등하게 뽑는다.
  - ELO는 에피소드 **마지막 단계 보상의 부호**로 승패를 정한다. 이 환경의 마지막 단계에는 마지막 투구의 투구 보상(타자 컨택 +0.5 등)과 타석 결과 보상이 함께 들어간다.
  - 그래서 2단계에서는 약한 땅볼도 "타자 승"이 된다. 3단계에서도 강한 타구가 아웃되면 "타자 승"이 된다.
  - 대신 `Matchup/*`(타석 결과 보상의 부호)과 고정 상대 평가 지표를 본다.
  - 마지막 단계에 결과 보상만 오게 하려면 타석이 끝날 때 결정을 한 번 더 받아야 한다. 투수는 타석당 결정이 2.2번이라 결정의 약 30%가 효과 없는 결정이 된다. 또 투수 관측에는 타석 종료를 구분할 값이 없어 관측 계약도 바꿔야 한다. 표시용 값 때문에 학습 자료를 바꾸지 않았다.
