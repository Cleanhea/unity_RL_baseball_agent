# 야구 시뮬레이션 문서 안내

## 학습 커리큘럼과 단계 씬 (2026-09-26)

세 단계 학습을 위한 씬과 Agent를 구성했다. 전체 구조는 [학습 단계와 씬 구성](training-curriculum.md)에 있다.

| 단계 | 씬 | 학습 대상 | 계약 문서 |
| --- | --- | --- | --- |
| 1 타자 | `Scenes/Training/Stage1_Batter` | 타자. 존 중앙 직구, 구속 120~150 km/h | [타자 Agent](batter-agent.md) |
| 2 타자+투수 | `Scenes/Training/Stage2_BatterPitcher` | 타자·투수. 구종 5종·구속·위치 | [투수 Agent](pitcher-agent.md) |
| 3 전체 | `Scenes/Training/Stage3_FullTeam` | 타자·주자·투수·수비 9명 | [수비·주루 Agent](fielding-agents.md) |

볼카운트·타석·주자 여러 명·결과 보상은 [상황 규칙](game-situation.md)에 있다. 단계 자동 전환과 실행 방법은 [Training/README.md](../Training/README.md)에 있다. `BaseballPlayground`는 Agent 없는 수동 조작 씬으로 되돌렸다.

2026-09-27에 2·3단계용 [고정 상대 평가](training-curriculum.md#고정-상대-평가-2026-09-27)와 [셀프플레이 설정](training-curriculum.md#동시-학습과-셀프플레이)을 더했다. 아래 "현재 단계에서 하지 않는 일"의 셀프플레이·다중 에이전트 학습 제한은 이 커리큘럼에 한해 사용자가 해제했다.

## 타자 ML-Agents 연결 (2026-09-24)

`BatterAgent`가 [타자 Agent 계약](batter-agent.md)의 타자 상태 벡터 16값(볼카운트·아웃·주자 포함)과 공을 보는 레이 센서 `BallEye`(51개 레이, 스택 3), 7개 연속·1개 이산 행동을 사용한다. 공의 정답 위치·속도는 관측하지 않는다. 학습기·모델이 없을 때는 스윙하지 않는 중립 행동이다. 투구별 `BatterRewardTracker` 보상을 `AddReward`로 전달하고, 한 타석이 끝나면 결과 보상을 더해 `EndEpisode`를 호출한다. 단계 씬의 타자 루트(`Actors/Batter`)에 Agent/Behavior Parameters를 연결했다. ML-Agents 패키지는 4.0.3이며 단계별 학습 YAML이 있다.

## 타자 강화학습 보상 설계 초안 (2026-09-24)

사용자가 지정한 접촉·미접촉·타구 속도·높은 장시간 비행·파울·홈런의 여섯 보상 조건을 [타자 보상 설계](batter-reward-design.md)에 사건별 수치와 지급 규칙으로 정리하고 `BatterRewardTracker` 계산기로 구현했다. 보상 증분과 성분별 합계를 제공하며 `PlayDirector`의 기존 사건을 구독한다. Agent 연결은 위 최신 절을 따른다.

## 최신 구현 상태 — 볼/스트라이크 (2026-09-24)

투구마다 `Ball`/`CalledStrike`/`SwingingStrike`/`Foul`/`InPlay`를 판정한다. 존은 오각형 홈플레이트 위 0.48~1.03 m(Statcast 평균, 설정 가능)이고 공의 일부만 닿아도 스트라이크, 바운드된 공은 볼이다. 투구 위치는 기본 머신 목표 고정이며 설정 `RandomAroundZone`(존 통과율 약 45%)이나 `RequestThrowPitch(Vector2)`로 바꾼다. `GetPitchCall()`, `PitchCalled`, HUD 존 패널로 확인한다. 볼카운트는 아직 없다. 원본 씬의 3D 존 틀은 `Tools > Baseball Simulation > Align Strike Zone And Plate Visuals`를 실행하고 저장해야 규칙 존과 맞는다. 규칙은 [환경 명세](environment-spec.md), 검증은 [검증](verification.md) 12.7.

## 최신 구현 상태 — 타구 판정과 물리 (2026-09-23)

직구와 타구에 공기 항력·회전 양력을 적용하고, 지면 반발·마찰·구름 저항 및 충돌 가능한 외야 펜스를 추가했다. 투구 해석기는 실제 비행과 같은 고정 시간 적분으로 스트라이크존을 겨냥한다. 타구는 첫 닿음과 1·3루 통과 위치로 페어/파울을 가리고, 공중 펜스 통과는 `HomeRun`, 페어 타구의 바운드 후 펜스 통과는 `GroundRuleDouble`로 종료한다. 스윙 파워 배율 1.2~2.0은 설정 시드로 재현할 수 있다. `GetBattedBallSnapshot()`과 HUD에서 타구 속도·각도·회전·비거리·판정을 조회한다.

원본 `BaseballPlayground.unity`에는 주자와 96개 Collider로 만든 외야 펜스를 저장했다. 이 절은 아래의 타구 장외 처리·파울 미구현 설명보다 우선한다. 판정 범위와 제한은 [환경 명세](environment-spec.md), 실행 결과는 [검증](verification.md)의 최신 기록을 본다. 수비, 세이프/아웃, 홈런·인정 2루타의 주자 베이스 수여는 아직 없다.

## 최신 구현 상태 — 주루 (2026-09-23)

타자주자 주루를 추가했다. 접촉하면 타자가 타자주자로 바뀌어 1루까지 자동으로 달리고 멈춘다. 1루 이후에는 **진루(F) / 귀루(B)** 판단만 `RequestRunnerDecision(RunnerDecision)`으로 받는다. 판단 에이전트는 아직 없고 같은 명령에 나중에 연결한다. 홈을 밟으면 `RunScored`가 된다. 주자는 원본 씬에 저장돼 있다. 수비·아웃/세이프는 아직 없으며, 타구 종료 규칙은 위의 최신 절을 따른다. 주루 규칙과 당시 검증은 [환경 명세](environment-spec.md) 및 [검증](verification.md) 12.5를 본다.

## 최신 구현 상태 — 타자 (2026-09-18)

현재 우선 목표는 **타자 우선 학습을 위한 네 항목의 독립 제어·평가**다. [타자 평가 명세](batting-evaluation.md)에 제어 API, 기준 자세, 오차/점수 정의와 조작법을 정리했다. 타자 X/Z와 상대 배트 위치 X/Y/Z를 투구 전에 정하고, 스윙 시점과 실제 평면 각도를 결정한다. HUD와 `GetBattingEvaluation()`에서 네 항목을 각각 읽는다. 타자 Agent 연결은 최신 절을 따르며 실제 학습은 아직 없다.

단계 3 타격을 추가했다. `BaseballPlayground.unity`에 기본 도형 타자·배트와 Director 참조를 저장했다. Play Mode에서 **P 투구 → Space 스윙**, 방향키로 실제 스윙 평면 각도, R로 초기화한다. 투구 전 WASD로 타자, IJKL/UO로 배트 위치를 조절한다. 현재 공기력 기본값의 투구는 약 0.54초에 목표 평면에 도착하며, 검증용 스크립트의 기준 스윙 시작은 약 0.40초다. 수동 키 입력에서는 프레임 시점에 따라 차이가 날 수 있다.

`RequestSwing(SwingCommand)`를 통해 스크립트도 같은 동작을 요청한다. 접촉 후 `BattedBallInFlight`에서 타구를 관찰하며 장외 또는 투구 시작 기준 12초에 종료한다. 주루·수비·페어/파울 결과 판정은 후속 단계다. 아래 2026-09-11/12 현황은 당시 기록이며 타격 미구현이라는 설명은 이 최신 기록으로 대체한다. 실제 검증 결과는 `verification.md` 12.3을 참고한다.

## 프로젝트 목적

장기적으로는 야구장 환경에서 투구, 타격, 주루, 수비를 수행하는 강화학습 에이전트를 연구한다. 다만 이 설명은 장기 비전이며 현재 구현 요구사항이 아니다.

현재 단계의 목표는 다음 한 문장으로 고정한다.

> 학습 모델 없이 Unity에서 투구, 타격, 주루, 수비, 결과 판정, 초기화로 이어지는 한 플레이를 실행하고 검증할 수 있다.

수동 입력, 단순 스크립트와 타자 Agent가 동일한 환경 명령을 호출한다. 환경의 입력·상태·이벤트·초기화 경계를 분명히 한다. 타자 Agent의 첫 관측·행동 공간과 보상 가중치를 정의했으며, 학습 설정과 성능 평가는 후속 작업이다.

## 현재 단계에서 하지 않는 일

- 강화학습 알고리즘·모델, 타자 이외의 보상 함수, 신경망·하이퍼파라미터
- Python 학습 환경, 학습 서버, 학습 YAML, 셀프플레이와 다중 에이전트 학습
- 타자 이외의 ML-Agents Agent 연결과 학습 실행
- 구종 확장과 정밀 공기역학, 정밀 배트 물리
- 정규 경기의 전체 규칙, 이닝·팀·복수 주자 운영
- 고품질 캐릭터·모션 캡처·복잡한 애니메이션
- 멀티플레이, 관중 캐릭터, 중계, 운영용 UI
  - 예외: 사용자 요청으로 기본 도형 관중석 구조물은 표시 전용으로 만들었다. 관중·중계·UI는 그대로 제외한다.

관련 패키지가 이미 존재하더라도 이 범위를 이유로 삭제하거나 재설정하지 않는다. 향후 필요할 것이라는 추측만으로 현재 환경에 불필요한 프레임워크도 만들지 않는다.

## 문서 읽는 순서

1. [환경 명세](environment-spec.md): 무엇을 만들고 어떤 규칙으로 판정하는지 확인한다.
2. [아키텍처](architecture.md): 객체 책임, 데이터 흐름, 입력·조회·이벤트 경계를 확인한다.
3. [구현 계획](implementation-plan.md): 실행 가능한 작은 작업 순서와 각 완료 조건을 따른다.
4. [검증](verification.md): Unity Editor에서 시나리오별 기대 결과를 확인한다.

저장소에서 작업하는 에이전트는 루트의 `AGENTS.md`를 가장 먼저 읽는다. `CLAUDE.md`는 같은 공통 지침을 간결하게 참조한다.

## 조사 기준일과 현재 프로젝트 사실

아래 내용은 2026-09-11에 파일을 읽어 확인한 사실이다.

| 항목 | 확인된 현재 상태 |
| --- | --- |
| Unity | `6000.5.2f1` |
| 렌더 파이프라인 | URP `17.5.0`; PC/Mobile 렌더 파이프라인 에셋 존재 |
| 물리 기본값 | 3D 중력 `(0, -9.81, 0)`, Fixed Timestep `0.02 s`, 자동 시뮬레이션, Enhanced Determinism 꺼짐 |
| 입력 | Input System `1.19.0`과 템플릿 `InputSystem_Actions.inputactions` 존재; 야구 전용 액션은 없음 |
| 빌드 씬 | `Assets/Scenes/SampleScene.unity` 한 개가 활성화됨 |
| 씬 내용 | Main Camera, Directional Light, Global Volume만 존재 |
| 프로젝트 코드 | `Assets/Scenes/Temp.cs`는 내용이 비어 있는 템플릿 `MonoBehaviour`; 야구 기능 없음 |
| 프리팹·야구 에셋 | 프리팹, 야구장, 공, 선수, 야구 전용 머티리얼이 확인되지 않음 |
| 직렬화 | Unity 에셋 텍스트 직렬화 사용 |
| 관련 패키지 | AI Navigation `2.0.13`, Test Framework `1.7.0`, Unity MCP Git 패키지 등이 설치 선언됨 |
| ML-Agents | `Packages/manifest.json`에 설치 선언이 없음 |
| 기존 문서 | 루트 `Readme.md`에 장기 강화학습 비전만 있으며, 기존 `docs/`, `AGENTS.md`, `CLAUDE.md`는 없었음 |
| 버전 관리 | 작업 폴더에 `.git`이 없어 Git 상태 확인 불가 |

현재 `SampleScene`의 카메라·조명·Global Volume과 URP 설정은 출발점으로 재사용할 수 있다. 일반 입력 액션은 설치 현황으로 보존하되 야구 명령으로 이미 연결됐다고 보지 않는다. `Temp.cs`도 구현 자산으로 간주하지 않는다.

## 구현 상태와 계획의 구분

구현 계획의 **단계 1(야구장과 기본 배치)까지 구현**되어 있다. `Assets/BaseballSimulation/`에 야구장 씬(`BaseballPlayground.unity`), 기준점을 소유하는 `FieldLayout`, 설정 데이터 `BaseballEnvironmentConfig`와 기본 에셋, 씬을 만드는 Editor 메뉴가 있다.

2026-09-12에 사용자가 명시적으로 요청한 범위로 **단계 2를 피칭머신(직구 하나, 중앙 통과, 재투구, 초기화)만큼 부분 구현**했다. `Ball`(Rigidbody/SphereCollider), `BallController`, `PlayDirector`(Ready/PitchInFlight/Ended), `ManualPlayController`(P=투구, R=초기화), `DebugPresenter`(OnGUI HUD), 씬의 피칭머신 외형과 스트라이크존 표시가 있다. 공 소유권(포구·송구), 정지 기준 기반 `DeadBall`, 타격, 주루, 수비, 플레이 결과 판정, 볼카운트, 학습용 디버그 HUD 확장은 아직 **계획 상태**다.

[검증 문서](verification.md)에서 단계 1의 실행 기록은 12.1장, 피칭머신의 실행 기록은 12.2장에 있고, 그 밖의 시나리오는 통과 결과가 아니라 향후 실행 절차다. 두 기록 모두 배치 모드에서 확인한 항목과 대화형 Unity Editor에서 확인해야 할 항목이 나뉘어 있다.

## 공통 설계 가정

- 3D 환경이며 `1 Unity unit = 1 m`로 사용한다.
- 홈 플레이트를 지면 좌표 원점으로 하고, `+Y`는 위, `+Z`는 홈에서 중견수 방향, `+X`는 홈에서 1루 쪽으로 둔다.
- 실제 야구 치수를 참고한 기본값을 사용하되, 빠른 검증을 위해 판정 반경과 속도는 설정 데이터에서 조절할 수 있게 한다.
- 첫 구현은 타자주자 1명, 타구 처리 수비수 1명, 1루 수비수 1명의 최소 구성이다.
- 타격은 배트의 접촉 구간을 검출한 뒤 타이밍·각도로 타구 속도를 계산해 공에 적용하는 단순 혼합 모델을 사용한다.
- 기본 플레이는 1루 세이프/포스 아웃에서 끝낸다. 득점 검증 시에는 종료 목표를 홈으로 바꾼 단일 주자 시나리오를 사용한다.
- 고정 시드는 입력 조건 재현용이다. 운영체제, Unity/물리 엔진 버전, 하드웨어가 다른 실행 사이의 완전한 물리 동일성을 보장하지 않는다.
