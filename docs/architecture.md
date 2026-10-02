# 기본 야구 환경 아키텍처

## 최신 연결 — 학습 단계 컨트롤러와 수비 (2026-09-26)

[학습 단계](training-curriculum.md) 구현으로 책임이 다음처럼 나뉜다.

`Training/auto_curriculum.py`는 Unity 외부에서 단계별 실행 파일 빌드·ML-Agents 학습기 실행·완료 확인·다음 단계 시작을 맡는다. 빌드는 `Training/UnityBuild/Editor/CurriculumPlayerBuild.cs`를 원본과 분리된 임시 Unity 프로젝트에 복사해 수행한다. 원본 Editor의 학습 씬과 `TrainingEnvController`는 자기 단계의 에피소드만 담당한다. 3단계도 일반 `mlagents-learn`으로 실행한다. `Training/mlagents_extensions`의 등록된 POCA 확장이 수비 YAML의 `baseball_fielder_poca`에서 선택되어 수비 정책의 환경 행동·ONNX 출력만 기존 `/3` 스케일 후 벡터 크기로 제한한다. 설치 패키지 소스를 수정하지 않고 저장소 코드를 Python 환경에 한 번 등록한다. 원본 optimizer 행동·체크포인트 구조는 유지한다.

| 구성요소 | 책임 | 소유하지 않는 것 |
| --- | --- | --- |
| `TrainingEnvController` (씬마다 하나, `Systems/`) | 단계별 결정 요청(`Academy.AgentPreStep`), 1단계 스크립트 투구, 투구·타석 종료 판단과 초기화 요청, 타석 결과 보상, 수비·주자 그룹(`SimpleMultiAgentGroup`), 수비 결정마다 그룹 포구·포스 커버와 개인 쫓기·내야 커버 포텐셜 차분 지급·종료/중단 정산, 첫 수비 결정 뒤 고정 단계마다 역할 면제·거리·시간에 따른 개인 자리 감점과 합 기록, 새 타석 무작위 상황, 2단계부터 고정 상대 평가 타석 선택(기준 투수 투구, 타석 사이 타자 정책의 모델 추론 전환·복원) | 경기 규칙·물리·판정·카운트 |
| `BenchmarkPitcher` (정적) | 평가 타석의 고정 구종 비율·구속·위치 분포 → `PitchCommand` | 투구 물리·판정 |
| `GameSituation` (Director 소유) | 볼카운트·아웃·베이스 점유·득점, 볼넷·삼진·반 이닝, 플레이 결과 반영 | 물리·Agent |
| `PlayDirector` | 기존 역할 전체와 새 명령(`RequestThrowPitch(PitchCommand)`, `RequestFielderMove/Throw`, `RequestScriptedBattedBall`), 포구·송구·포스/태그 아웃·`RunnerSafe` 판정 | Agent·보상 |
| `PitchPhysics` (정적) | 구종 회전 벡터, 휘는 공 2축 조준 해석기, 통과점 적분 | |
| `FielderController` (수비수 루트) | 이동 명령을 속력·가속 한도로 적분, 글러브 위치 | 포구·아웃 판정 |
| `BallController` | 기존 역할과 수비수 소유(`Hold`/`MoveHeld`/`Throw`, `HolderIndex`) | |
| `BatterAgent`·`PitcherAgent`·`RunnerAgent`·`FielderAgent` (각 선수 루트) | 관측과 행동 → `PlayDirector` 명령 | 결정 시점(컨트롤러가 정함) |
| `BatterRewardTracker`·`PitcherRewardTracker`·`PlayOutcomeRewards` | 투구 단위 보상, 3단계 결과 보상 | 판정 |
| `TrainingStats` (컨트롤러 소유) | 투구·타석·플레이가 끝날 때 `PlayDirector`·Agent 읽기 전용 값으로 TensorBoard 지표를 만들어 ML-Agents `StatsRecorder`로 보냄(학습기 연결 시에만). 평가 타석은 `Benchmark` 묶음, Agent끼리 대결한 타석은 일반 지표와 `Matchup` | 보상·관측·판정 |

- Agent는 Decision Requester 없이 컨트롤러의 `RequestDecision()`/`RequestAction()`으로만 결정한다.
- 명령은 모두 `PlayDirector` 버퍼에 쌓였다가 `FixedUpdate`에서 적용된다. 수동·스크립트·Agent가 같은 경계를 쓴다.
- 수비수 배열이 비어 있으면 3단계 규칙은 실행되지 않는다. 그래서 수동 씬·1·2단계 동작은 이전과 같다.
- 타구 시작은 실제 접촉과 시나리오 타구가 같은 `StartBattedBall`을 쓴다.

## 최신 연결 — 타자 ML-Agents Agent (2026-09-24)

`BatterAgent`는 `CollectObservations`에서 `PlayDirector.GetSnapshot()`과 `GetBattingEvaluation()`을 읽어 타자 상태와 볼카운트·아웃·주자 16값 벡터 센서를 채운다. 공은 자식 `BallEye`의 `RayPerceptionSensorComponent3D`가 `Ball` 태그로 감지하며, 이 센서는 `Use Child Sensors`로 수집된다. 공 정답 위치·속도는 Agent 관측에 넣지 않는다(2026-09-26). 스냅샷의 공 값은 판정·디버그·검증용으로 그대로 제공한다. `OnActionReceived`는 투구 전 자세 명령, 투구 요청, 투구 중 스윙 명령만 Director에 전달한다. `PlayReset` 후 보상 수집을 시작하고 `BatterRewardTracker`의 증분/완료 이벤트를 `AddReward`/`EndEpisode`로 전달한다. 씬의 `BehaviorParameters`와 `DecisionRequester`는 [타자 Agent 계약](batter-agent.md)에 맞춘다. 기존 수동 입력과 자동 반복은 Agent가 활성화된 동안 꺼 둔다.

배치(2026-09-26): Agent와 두 ML-Agents 컴포넌트는 `BatterController`가 있는 타자 루트 오브젝트에 둔다. 자식 센서가 선수 몸을 따라 수집되게 하려는 것이며, 향후 투수·수비 Agent도 각 선수 루트에 둔다. 같은 오브젝트에 있어도 소유권은 바뀌지 않는다. Agent는 `PlayDirector` 명령·스냅샷만 사용하고 `BatterController`를 직접 호출하지 않는다. Director의 초기화 요청과 입력 전환은 플래그/직렬화 필드라 Agent와 Director의 `Awake`/`OnEnable` 순서에 의존하지 않는다.

## 타자 보상 계산기 (2026-09-24)

`BatterRewardTracker`는 `PlayDirector` 밖에서 `BallBatContact`, `BattedBallCalled`, `PitchCalled`를 구독한다. Agent가 `PlayReset` 뒤 `BeginEpisode()`로 한 투구의 보상 상태를 시작하고, 계산기는 `RewardAdded(float)`와 `EpisodeCompleted(BatterRewardSnapshot)`을 발행한다. `Dispose()`로 구독을 해제한다. 경기 결과와 타자 평가 점수의 소유권은 그대로 Director와 BatterController에 있고, 보상 계산기는 이를 읽기만 한다. 가중치·판정별 지급 규칙은 `batter-reward-design.md`를 따른다.

## 최신 연결 — 볼/스트라이크 판정 (2026-09-24)

`Scripts/Core/StrikeZone.cs`는 규칙 존 기하(오각형 플레이트, 플레이트 좌표 변환, 공-존 교차)만 가진 순수 정적 클래스다. `PlayDirector`가 투구 위치 결정(설정 방식 또는 `RequestThrowPitch(Vector2)`), 환경 전용 투구 위치 난수원, 매 고정 단계의 존 통과·플레이트 통과 위치 기록, `PitchCall` 확정을 맡는다. 치지 못한 공은 투구 종료 때, 맞힌 공은 `BattedBallJudge` 판정 때 정한다. 조회는 `GetPitchCall()`의 `PitchCallSnapshot`, 이벤트는 `PitchCalled(PitchCall)`이다. `RequestResetPlay(int seed)`는 투구 위치(시드+1)와 스윙 파워(시드) 난수원을 다시 만든다. `DebugPresenter`는 스냅샷만 읽어 포수 시점 존 패널을 그린다.

`StrikeZoneSceneSetup`(Editor)은 3D 존 틀과 홈플레이트 표시를 규칙 존 위치로 옮기는 메뉴이며 빌더도 같은 배치를 쓴다. 판정은 씬 표시 객체를 읽지 않는다.

## 최신 연결 — 타구 물리와 판정 (2026-09-23)

`BallController`가 Rigidbody 속도·회전, 공기력, 지면/펜스 접촉 기록을 소유한다. `PlayDirector`는 `TrySolvePitch`로 고정 단계 적분에 맞춘 투구 속도와 회전을 계산하고, 접촉 때 타구 초기 속도·회전을 공에 전달한다. `BattedBallJudge`는 이전/현재 공 위치와 첫 닿음 사실로 페어·파울·홈런·인정 2루타를 판단한다. Director만 `PitchEndReason`을 확정하고 `BattedBallCalled`를 발행한다. 판정 조회는 `GetBattedBallSnapshot()`이며 HUD는 이 스냅샷을 읽는다.

`FenceSceneSetup`이 Editor에서 96개 충돌 펜스를 `Field/OutfieldFence`에 연결하고, `BaseballPlaygroundBuilder`도 이를 호출한다. 원본 씬에도 펜스를 저장했다. 설정값은 `BaseballEnvironmentConfig`에 있으며 공기력 켜기/끄기, 항력·회전, 지면 반발·마찰·구름 저항, 펜스 높이, 시드와 스윙 파워 범위를 조절한다. 수비나 주자 자동 수여는 이 연결에 포함되지 않는다.

## 최신 구현 변경 — 주루 (2026-09-23)

`Scripts/World/RunnerController.cs`가 타자주자 한 명의 경로 이동, 진루/귀루 판단 적용, 베이스를 밟은 사실 보고를 맡는다. 아웃/세이프와 플레이 결과는 판단하지 않는다. `PlayDirector`는 Inspector의 선택 참조 `runner`를 가지며, 시작할 때 설정과 `FieldLayout`을 전달한다. 참조가 없으면 주루 없이 기존 동작을 유지한다. 규칙은 `environment-spec.md`의 최신 주루 절을 따른다.

- 명령: `RequestRunnerDecision(RunnerDecision)`. 4.1의 `SetRunnerTarget(BaseId)` 계획은 사용자 요청에 따라 진루/귀루 두 판단으로 대체했다. 다음 FixedUpdate에서 이전 물리 구간 관찰 → 자세 → 투구 → 스윙 → 주루 판단 순서로 적용하며, 같은 틱의 초기화가 판단을 취소한다.
- 진행: 접촉한 틱에 Director가 `runner.Begin(타자 위치)`와 `batter.SetVisible(false)`를 호출한다. 이후 `BattedBallInFlight`의 각 고정 단계는 타자 Tick → 주자 Tick → 득점/장외/시간 초과 확인 순서다. 주자는 Rigidbody·Collider 없이 Transform만 옮긴다.
- 조회와 이벤트: `GetRunnerSnapshot()`은 읽기 전용 `RunnerSnapshot`을 돌려준다. `RunnerController.BaseReached`를 Director가 `RunnerBaseReached(BaseId)`로 다시 발행한다. 홈 도착은 `PitchEndReason.RunScored`로 기록한다. 타구 판정 조회와 이벤트는 위 최신 연결을 따른다. 이벤트 처리기에서는 명령만 요청한다.
- 향후 학습 연결: 에이전트는 수동·스크립트 입력과 나란한 입력원으로서 이벤트나 스냅샷을 읽고 `RequestRunnerDecision`만 호출하면 된다. RunnerController를 ML-Agents 타입으로 바꾸지 않으며, 관측·행동 공간·보상은 아직 정하지 않았다.
- 씬 연결: `Tools > Baseball Simulation > Add Batter-Runner To Current Scene`(`RunnerSceneSetup`)이 `Actors` 아래에 `BatterRunner`를 만들고 Director 참조를 연결한다. 표시는 Collider 없는 Capsule/Sphere이며 기본 비활성이다. 이미 연결돼 있으면 새로 만들지 않는다. `BaseballPlaygroundBuilder`도 이 함수를 호출한다.
- 검증 도구: `Tools > Baseball Simulation > Verify Runner (Paused Play Mode)`(`RunnerVerification`).

## 최신 구현 변경 — 2026-09-18

네 항목 평가 확장: `BatterSetupCommand`(타자 X/Z·상대 손잡이 X/Y/Z), `BattingEvaluation` 읽기 전용 값 타입, `RequestBatterSetup`/`GetBattingEvaluation`/`BattingEvaluated`를 추가했다. Director는 이전 물리 구간 관찰 → Ready 자세 명령 → 투구 → 스윙 순서로 실행하고 초기화는 모두 취소한다. 발사한 틱의 시간을 미리 더하던 1틱 오차를 제거했다. 초기화는 타자/배트 자세도 복원한다. 입력 소스는 ManualInputEnabled로 구분한다. 상세 책임과 평가 정의는 `batting-evaluation.md`를 따른다.

BatterController는 목표 고정 손잡이를 제거하고 몸 위치에 상대 손잡이를 더한다. 실제 스윙 평면과 접촉 시 진행 접선이 출력 타구를 결정한다. 전체 스윙을 공간 검사하며 타이밍 허용폭은 점수 계산에만 사용한다. 배트 표시와 판정은 같은 기하 함수를 공유한다. 아래 이전 버전의 중간 시각 제한/단순 출력각 설명은 대체된다.

스윙 시작·끝 각도는 BaseballEnvironmentConfig가 소유한다. BatterController의 표시·접촉 Direction과 세부 검사 간격이 이 값을 공유하며, 고정 160° 회전 가정은 제거했다. 유효성 검사는 역방향/영폭 스윙 및 비유한·범위 밖 각도를 거부한다.

`Scripts/World/BatterController.cs`가 스윙 자세, 동시각 공/배트 휩쓸림 근사, 타구 속도 계산을 맡는다. 현재 파일 수를 줄이기 위해 별도 Actors 폴더나 Resolver는 만들지 않았다. `PlayDirector`가 Inspector의 명시적 타자 참조를 가지고 시작 시 설정과 목표점을 전달한다. `RequestSwing(SwingCommand)`는 다음 FixedUpdate에서 이전 물리 구간 판정을 마친 뒤 수락된다. 초기화가 같은 틱의 투구·스윙 요청보다 우선한다. 중복 스윙과 잘못된 상태·비유한 각도를 거부한다.

`SwingStarted`, `BallBatContact(Vector3)` C# 이벤트와 `HasSwung`, `HasContact`, `ContactQuality` 읽기 전용 속성을 추가했다. HUD는 기존 PitchSnapshot과 이 속성들을 읽는다. 조준 표시는 ManualPlayController가 Director의 Aim 속성에 전달하는 입력 미리보기이며 접촉 계산은 수락 당시 SwingCommand 값을 쓴다. 사건 시각·틱·시드를 포함하는 전체 이벤트 계약은 후속 작업이다.

`BatterSceneSetup.AddBatter`는 현재 씬에 타자 도형을 추가하고 참조를 연결하는 Editor 메뉴다. 기존 타자 참조가 있으면 중복 생성하지 않는다. `BaseballPlaygroundBuilder`도 이 함수를 호출한다. 표시 도형에는 Collider가 없고 기존 머티리얼을 재사용한다. 런타임 이름 검색·자동 생성은 하지 않는다. 현재 씬에는 Editor API로 타자와 참조를 저장했다.

설정 데이터에 스윙 0.25초, 접촉 중심 반폭 0.05초, 접촉 반경 0.09m, 배트 1m, 타구 속력 8–32m/s, 타구 관찰 제한 12초를 추가했다. 아래 타격 미구현 설명은 이전 단계 기록이며 최신 범위는 환경 명세의 최신 구현 절을 따른다.

## 1. 목적과 현재 제약

이 문서는 `docs/environment-spec.md`의 계획을 현재 Unity 프로젝트에서 구현하기 위한 최소 구조를 정의한다. 목표는 확장 가능한 거대 프레임워크가 아니라, 투구부터 초기화까지 한 플레이를 읽고 고칠 수 있는 책임 분리다.

2026-09-11 현재 프로젝트에서 확인한 제약은 다음과 같다.

- Unity `6000.5.2f1`, URP `17.5.0`, 3D 물리와 Input System을 사용할 수 있다.
- 빌드에 포함된 `SampleScene`에는 카메라, 조명, Global Volume만 있다.
- 야구 전용 스크립트·프리팹·설정 데이터는 없다.
- `Temp.cs`는 빈 템플릿이라 재사용할 기능이 없다.
- 2026-09-11 당시 ML-Agents 설치 선언은 없었다. 타자 Agent 연결로 2026-09-24에 4.0.3을 추가했다.
- 에셋은 텍스트 직렬화되지만 씬·프리팹 변경은 Unity Editor를 우선해 참조와 GUID를 보호한다.

## 2. 제안 폴더

구현이 시작되면 기존 템플릿 파일을 임의 정리하지 않고 다음 영역을 추가한다.

```text
Assets/
└─ BaseballSimulation/
   ├─ Scenes/
   │  └─ BaseballPlayground.unity          # 있음
   ├─ Scripts/
   │  ├─ Core/
   │  │  ├─ PlayDirector.cs                # 있음 (피칭머신 범위: Ready/PitchInFlight/Ended)
   │  │  └─ SimulationContracts.cs         # 있음 (BaseId, PlayState, PitchEndReason, PitchSnapshot)
   │  ├─ World/
   │  │  ├─ FieldLayout.cs                 # 있음
   │  │  └─ BallController.cs              # 있음
   │  ├─ Actors/
   │  │  ├─ BatterController.cs
   │  │  ├─ RunnerController.cs
   │  │  └─ FielderController.cs
   │  ├─ Input/
   │  │  ├─ ManualPlayController.cs        # 있음 (P=투구, R=초기화)
   │  │  └─ ScriptedPlayController.cs
   │  ├─ Presentation/
   │  │  └─ DebugPresenter.cs              # 있음 (OnGUI HUD)
   │  ├─ Editor/
   │  │  └─ BaseballPlaygroundBuilder.cs   # 있음
   │  └─ Settings/
   │     └─ BaseballEnvironmentConfig.cs   # 있음
   ├─ Config/
   │  └─ DefaultBaseballEnvironment.asset  # 있음
   ├─ Materials/                           # 있음
   └─ Prefabs/              # 재사용 가치가 생긴 객체만 승격 (아직 없음)
```

`# 있음` 표시가 없는 항목은 아직 만들지 않았다. 폴더도 실제로 쓸 때 만든다.

`Scripts/Editor/`는 Unity가 Editor 전용 어셈블리로 컴파일하는 폴더다. `BaseballPlaygroundBuilder`는 씬과 기본 에셋을 Editor API로 만들고 기준점을 검증한다. 씬 YAML을 손으로 쓰지 않기 위한 도구이며 런타임 환경 동작에는 관여하지 않는다.

`BaseballPlayground.unity`는 기존 `SampleScene`의 카메라·조명·Global Volume 구성을 출발점으로 삼되 별도 씬으로 저장하는 것을 기본 선택으로 한다. 이렇게 하면 템플릿 씬을 보존하면서 환경 전용 루트를 명확히 할 수 있다. 첫 구현에서 한 번만 쓰는 도형을 모두 프리팹으로 만들 필요는 없다. 공이나 선수처럼 반복 생성·참조할 이유가 확인될 때만 프리팹으로 승격한다.

파일 수는 상한이나 목표가 아니다. 매우 작은 구현에서는 `BatterController` 안에 타구 계산을 함께 둘 수 있다. 계산이 독립 검증을 방해할 정도로 커질 때만 `BattingResolver` 같은 순수 계산 클래스로 분리한다.

## 3. 런타임 구성과 책임

### 3.1 핵심 컴포넌트

| 컴포넌트 | 소유하는 책임 | 소유하지 않는 책임 |
| --- | --- | --- |
| `PlayDirector` | 플레이 상태·타이머·결과, 명령 유효성, 사건 중재, 초기화 순서 | 입력 장치 읽기, Rigidbody 세부 이동, UI 그리기 |
| `SimulationContracts` | 상태/결과 열거형, 명령 값, 이벤트 값, 읽기 전용 스냅샷 | MonoBehaviour 생명주기, 규칙 실행 |
| `FieldLayout` | 홈·베이스·투구점 참조, 주루 경로, 페어/파울·경계 질의 | 결과 확정, 선수 이동 |
| `BallController` | Rigidbody, 위치·속도·지면 접촉, 자유/소유 상태, 발사·획득·해제 | 플레이 종료 규칙, 수비 의사결정 |
| `BatterController` | 스윙 진행, 배트 접촉 구간, 타이밍·각도 기반 타구 속도 | 입력 키, 최종 결과 |
| `RunnerController` | 단일 주자의 경로 진행, 다음/최종 목표와 도착 사실 보고 | 아웃/세이프 우선순위 |
| `FielderController` | 지정 목표 이동, 포구 시도, 소유 공의 지정 대상 송구 | 학습 정책, 전체 플레이 판정 |
| `ManualPlayController` | Input System/키를 환경 명령으로 변환 | 직접 Rigidbody·상태 수정 |
| `ScriptedPlayController` | 시드와 시간표에 따른 검증 명령 제출 | 수동 입력과 다른 규칙 경로 |
| `DebugPresenter` | 스냅샷·이벤트를 읽어 HUD/Gizmo 표시 | 시뮬레이션 상태 변경 |
| `BaseballEnvironmentConfig` | 거리, 속도, 시간, 판정 반경, 기본 시드 | 플레이 중 가변 상태 |

`PlayDirector`가 모든 일을 직접 수행하지는 않지만 **환경 명령의 단일 입구와 결과의 단일 소유자**다. 하위 컴포넌트는 접촉·도착·소유권 같은 사실을 보고하고, 결과 우선순위는 Director 한 곳에서만 적용한다.

### 3.2 씬 계층 권장안

```text
BaseballEnvironment
├─ Field                                              # FieldLayout이 붙는다
│  ├─ Ground / FairTerritory / FoulTerritory
│  ├─ Infield
│  │  └─ InfieldDirt / InfieldGrass / PitcherMound / HomeCircle
│  ├─ FoulVisuals
│  ├─ Home / First / Second / Third / PitcherPlate    # 기준점 + 자식 Visual
│  ├─ PitchOrigin                                     # 있음
│  │  └─ PitchingMachine (Stand/Body/Muzzle)          # 있음, 표시 전용·Collider 없음
│  ├─ PitchTarget                                     # 있음
│  │  └─ StrikeZoneVisual (Top/Bottom/Left/Right/Center) # 있음, 표시 전용·Collider 없음
│  └─ PlayBoundary
├─ Stands                                             # 표시 전용, 경계 밖
│  └─ Tier_00..03 / Section_00..31
├─ Actors
│  ├─ Ball                                            # 있음
│  ├─ Batter                                          # 아직 없음
│  │  └─ Bat
│  ├─ BatterRunner
│  ├─ BallFielder
│  └─ FirstBaseman
├─ Systems
│  ├─ PlayDirector                                    # 있음 (Ready/PitchInFlight/Ended)
│  ├─ ManualPlayController                            # 있음 (P/R 키)
│  ├─ ScriptedPlayController                          # 아직 없음
│  └─ DebugPresenter                                  # 있음 (OnGUI HUD)
├─ Main Camera
├─ Directional Light
└─ Global Volume
```

씬의 Transform과 컴포넌트 참조는 Inspector에서 명시적으로 연결한다. 이름 검색이나 전역 `Find`를 런타임 핵심 연결 방식으로 삼지 않는다. 시작 시 필수 참조를 검증하고 누락되면 플레이를 시작하지 않은 채 명확한 오류를 낸다.

## 4. 입력, 상태 조회, 이벤트의 최소 경계

수동 입력과 스크립트 입력이 서로 다른 시뮬레이션 로직을 갖지 않게 다음 세 경계를 둔다.

```text
ManualPlayController ─┐
                      ├─> Play command sink / PlayDirector ─> Ball·Batter·Runner·Fielder
ScriptedPlayController┘                  ↑                         │
                                        └──── reported facts ─────┘
                                                  │
                          snapshot + environment events
                                                  │
                                 DebugPresenter (향후 학습 어댑터)
```

### 4.1 명령 경계

Director가 제공할 최소 명령은 다음과 같다. 메서드명은 구현 중 조정할 수 있지만 의미와 단일 진입 원칙은 유지한다.

- `ThrowPitch(PitchCommand)`: 시작점, 목표점, 초기 속도로 직구 시작
- `Swing(SwingCommand)`: 시작 시각, 좌우 각, 발사각으로 스윙 요청
- `SetRunnerTarget(BaseId)`: 주자의 최종 목표 지정
- `MoveFielder(FielderId, Vector3)`: 수동/스크립트 수비 이동 목표 지정
- `ThrowTo(FielderId or BaseId)`: 소유 선수가 송구할 대상 지정
- `SetPaused(bool)`: 시뮬레이션 진행 정지/재개
- `ResetPlay(optionalSeed)`: 현재 플레이를 중단하고 초기 배치 복원

명령은 요청 값일 뿐이다. Director는 현재 상태, 소유권, 대상 유효성을 검사해 수락 또는 거부하고, 입력 컨트롤러는 컴포넌트 Transform·Rigidbody·결과를 직접 바꾸지 않는다. 프레임 입력은 명령 큐에 넣고 고정 시간 단계에서 적용해 물리 시점과 맞춘다.

**현재 구현(피칭머신, 2026-09-12):** `PlayDirector.RequestThrowPitch()`와 `RequestResetPlay()`만 있다. 직구 하나이며 매번 같은 속력이라 `PitchCommand` 매개변수가 필요 없어 만들지 않았다. `Swing`, `SetRunnerTarget`, `MoveFielder`, `ThrowTo`, `SetPaused`는 아직 없다.

### 4.2 상태 조회

`GetSnapshot()`에 해당하는 읽기 전용 스냅샷은 플레이 상태, 결과, 시간, 시드, 공, 선수, 베이스, 주자를 한 시점 기준으로 반환한다. 외부 코드는 스냅샷을 통해 내부 컬렉션이나 Transform을 수정할 수 없어야 한다.

**현재 구현:** `PlayDirector.GetSnapshot()`은 `SimulationContracts.cs`의 읽기 전용 구조체 `PitchSnapshot`(상태, 경과 시간, 공 속력·위치, 목표 통과 오차 유무·값, 종료 사유, 마지막 거부 사유, 완료한 투구 수, 자동 반복 상태)을 돌려준다. `DebugPresenter`는 이 스냅샷만 읽는다. 결과·시드·선수·베이스·주자는 아직 스냅샷에 없다(해당 기능이 없어서다).

HUD는 매 프레임 최신 스냅샷을 표시할 수 있다. 스크립트 검증은 특정 이벤트를 기다린 뒤 스냅샷으로 결과를 확인한다. 향후 학습 연결이 필요해도 먼저 이 상태가 환경 검증에 충분한지 확인하고, 현재 단계에서 벡터 배열과 정규화를 추가하지 않는다.

### 4.3 이벤트 경계

공·타자·주자·수비 컴포넌트는 `BallGrounded`, `BallBatContact`, `BaseReached`, `BallCaught`, `BallPossessionChanged` 같은 사실을 Director에 보고한다. Director는 틱 단위로 모아 우선순위를 적용하고 `OutRecorded`, `RunScored`, `PlayEnded`를 포함한 환경 이벤트를 외부 구독자에게 발행한다.

이벤트 데이터는 변경 불가능한 값으로 만들고 시뮬레이션 시각, 고정 틱, 사건 순번을 포함한다. 이벤트 버스 패키지나 범용 메시징 프레임워크는 필요하지 않다. C# 이벤트 또는 Director의 작은 구독 API면 충분하다.

환경 이벤트 자체는 보상이 아니다. `BatterRewardTracker`가 타자 관련 환경 이벤트를 별도 정책으로 보상에 매핑하고 `BatterAgent`가 이를 학습 보상으로 전달한다.

## 5. 물리와 상태 갱신 흐름

### 5.1 일반 프레임 `Update`

- 활성 입력 컨트롤러가 장치 또는 시나리오 시간표를 읽고 명령을 큐에 넣는다.
- DebugPresenter가 최신 스냅샷을 읽어 텍스트를 갱신한다.
- 시각 전용 배트 보간이 물리 접촉 위치와 어긋나지 않도록 실제 접촉 판정용 Transform은 고정 단계의 진행값을 기준으로 한다.

### 5.2 고정 시간 단계 `FixedUpdate`

1. 이전 물리 단계에서 모인 사건을 Director가 우선순위대로 판정한다.
2. 초기화 요청이 있으면 다른 명령보다 먼저 초기화를 수행한다.
3. 현재 상태에 유효한 명령만 적용한다.
4. 주자와 수비수의 목표 이동, 스윙 접촉 구간 계산을 `fixedDeltaTime` 기준으로 진행한다.
5. 공은 Rigidbody와 Unity 3D 물리로 이동한다.
6. 충돌/Trigger 콜백은 결과를 즉시 확정하지 않고 발생 틱과 함께 사실 큐에 기록한다.
7. 다음 판정 시점에 같은 틱의 사건을 모아 환경 명세의 우선순위를 적용한다.

물리 콜백과 MonoBehaviour 실행 순서에 결과가 우연히 좌우되지 않게, 콜백 안에서 바로 `Ended`로 전환하지 않는다. 사건 판정이 한 고정 틱 늦게 보일 수 있지만 원래 발생 시각을 보존하므로 결과 비교에는 그 시각을 사용한다.

### 5.3 소유권과 송구

공 소유자는 BallController 한 곳에서 관리한다.

- `Acquire(owner)` 성공 시 기존 소유자를 해제하고 자유 물리를 중지한 뒤 공을 수신 지점에 둔다.
- 이미 다른 선수가 소유한 공은 두 번째 선수가 동시에 획득할 수 없다.
- `Release(velocity)`는 소유자를 먼저 비운 다음 Rigidbody를 활성화하고 속도를 적용한다.
- 모든 변경은 `BallPossessionChanged`를 한 번만 만든다.

포구 여부와 1루 수신 여부는 Fielder가 사실로 보고하되, BallController의 원자적 소유권 변경이 성공한 뒤에만 포구·수신 완료로 인정한다.

## 6. 타격 계산 경계

타격은 다음 두 부분만 분리한다.

- BatterController: 스윙 진행과 이전/현재 배트 접촉 구간을 제공한다.
- 타구 계산: 접촉 시각 오차와 명령 각도를 입력받아 방향·속도를 반환한다.

초기에는 타구 계산을 BatterController의 작은 순수 메서드로 둘 수 있다. 별도 컴포넌트나 물리 머티리얼 조합을 늘리지 않는다. 접촉 후 계산된 속도는 BallController의 단일 발사 API로 적용하고, 같은 스윙이 공을 여러 번 때리지 않도록 접촉 소비 플래그를 둔다.

## 7. 설정 데이터

`BaseballEnvironmentConfig` ScriptableObject 하나에 다음 범주의 기본값을 둔다.

- 필드 좌표와 경기 경계
- 공 크기·질량·투구·정지 기준
- 스윙 시간, 접촉 허용치, 타구 속도·각도 제한
- 주자·수비수 속도와 베이스/포구/수신 반경
- 송구 속도, 제한 시간, 기본 시드
- 디버그 표시 기본 토글

런타임 플레이 상태를 ScriptableObject에 쓰지 않는다. 시작 시 설정값을 읽고, 플레이 중 사용할 값은 환경 인스턴스가 보유한다. Play Mode 종료 후 에셋에 임시 값이 남는 것을 방지한다.

설정에는 유효 범위 검사와 의미 있는 단위를 표시한다. 잘못된 음수 속도, 0 이하 제한 시간, 역전된 최소/최대 값은 플레이 시작 전에 차단한다.

**현재 구현:** 필드 좌표·경기 경계·Gizmo 토글(단계 1)에 이어 공 반지름/질량, 투구 속력, 투구 제한 시간, 목표 뒤쪽 여유 거리, 스트라이크존 절반 폭/높이를 추가했다. 자동 반복 투구 토글·간격은 재사용 가치가 낮고 `PlayDirector` 하나만 쓰므로 Config가 아니라 `PlayDirector`의 Inspector 필드에 직접 둔다 — "쓰는 곳이 없는 설정을 미리 만들지 않는다"는 2장 원칙과, 여기 7장의 "재사용 카테고리만 Config에 모은다"는 취지를 함께 따른 결정이다.

## 8. 초기 배치와 재현성

Director는 시작 시 각 재사용 객체의 초기 Transform, 활성 상태, 공 물리 상태를 캡처하거나 명시적 Spawn Transform을 참조한다. 초기화는 객체별 `ResetState`를 정해진 순서로 호출한 후 Director 자신의 큐·타이머·결과를 비운다.

난수가 필요한 시나리오는 환경 전용 난수원과 명시적 시드를 사용한다. Unity 전역 난수 상태를 무심코 공유하지 않는다. 같은 시드, 같은 명령 시간표, 같은 설정은 문제 재현을 돕지만 Unity 물리가 서로 다른 실행 환경에서 비트 단위로 동일하다고 보장하지 않는다.

## 9. 향후 학습 연결 지점

향후 명시적인 학습 단계가 시작되면 학습 컨트롤러는 다음 경계에만 연결한다.

- 명령 경계로 투구, 스윙, 이동, 송구를 요청한다.
- 스냅샷을 읽어 그 단계에서 확정할 관측을 만든다.
- 환경 이벤트를 구독해 별도 보상 정책을 적용한다.
- 에피소드 시작/종료 때 기존 초기화와 결과를 사용한다.

BallController, RunnerController, FielderController를 ML-Agents 타입으로 바꾸지 않는다. `BatterAgent`는 수동·스크립트 컨트롤러와 같은 환경 명령을 사용하는 입력원이다. 타자 관측·행동과 보상 연결은 구현했으며 주루·수비 Agent와 학습 실행은 아직 없다.

## 10. 아키텍처 완료 확인

- 결과를 확정하는 곳과 공 소유권을 관리하는 곳이 각각 하나다.
- 수동/스크립트 입력이 동일한 명령 API를 사용하고 직접 상태를 수정하지 않는다.
- 물리 사실과 최종 야구 결과가 분리되어 같은 틱의 우선순위를 적용할 수 있다.
- 디버그 표시는 읽기 전용 스냅샷과 이벤트만 사용한다.
- 모든 런타임 객체가 초기화 계약을 지키고 이전 플레이 큐와 속도를 남기지 않는다.
- 설정값의 소유 위치와 단위가 하나로 정리되어 있다.
- 학습 패키지 없이 전체 환경을 실행할 수 있다.
- 새 추상화가 현재 수동·스크립트 검증에 실제 사용되지 않는다면 추가하지 않는다.
