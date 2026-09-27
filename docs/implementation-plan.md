# 기본 야구 환경 구현 계획

## 셀프플레이 준비와 고정 상대 평가 (2026-09-27)

사용자 요청으로 셀프플레이 적용 전에 필요한 항목을 추천 순서대로 구현했다. 기준은 [고정 상대 평가](training-curriculum.md#고정-상대-평가-2026-09-27)와 [동시 학습과 셀프플레이](training-curriculum.md#동시-학습과-셀프플레이)다.

1. **고정 상대 평가:** 2·3단계 새 타석의 10%는 기준 스크립트 투수(`BenchmarkPitcher`), 10%는 1단계 타자 모델(`Models/BenchmarkBatter_Stage1.onnx`, 추론 전용)이 상대한다. 성적은 `Benchmark Batter/`·`Benchmark Pitcher/`에 따로 기록한다. 일반 지표는 Agent끼리 대결한 타석만 넣는다.
2. **승패 지표:** ML-Agents `Self-play/ELO`는 마지막 단계 보상의 부호를 쓰므로 투구 보상이 섞인다. 그래서 타석 결과 보상의 부호로 `Matchup/*`을 기록한다. 궤적 구조와 관측·행동·보상은 바꾸지 않았다.
3. **스텝 비율:** 2단계 학습 기록에서 잰 결정 비율(타자 투구당 약 23, 투수 1)로 셀프플레이 교대 주기와 `max_steps`를 환산했다.
4. **3단계:** 셀프플레이는 타자·투수에만 넣고 주자·수비는 계속 학습한다.
5. **설정·자동화:** `Training/config/*_selfplay.yaml`, `auto_curriculum.py --self-play`, 별도 run-id.

## TensorBoard 야구 지표 (2026-09-27)

`TrainingStats`를 추가했다. 학습 중 투구 판정·스윙/컨택·타구·구종·타석 결과·3단계 플레이 결과가 ML-Agents `StatsRecorder`를 거쳐 TensorBoard에 기록된다. `TrainingEnvController`가 투구 종료·중단 때 호출하며, 보상·관측·행동 계약은 바꾸지 않았다. 지표 목록은 [TensorBoard 지표](training-curriculum.md#tensorboard-지표)에 있다.

## 학습 단계 자동 전환 (2026-09-27)

`Training/auto_curriculum.py`가 단계별 독립 실행 파일을 만들고, 각 단계의 모든 Behavior가 `max_steps`에 도달했는지 최종 모델·체크포인트·TensorBoard 기록으로 확인한 뒤 다음 학습을 시작하도록 추가했다. 이미 실행 중인 1단계에 붙는 모드도 있다. 자동 실행은 보상 점수와 무관하며, 실패 시 후속 단계를 시작하지 않는다. 실제 빌드·학습 연결의 확인 결과는 [검증 기록](verification.md)에 남긴다.

## 학습 커리큘럼 1~3단계 구성 (2026-09-26)

사용자 요청으로 세 단계 학습 씬을 차례로 구현·검증했다. 기준은 [training-curriculum.md](training-curriculum.md)다.

- **1단계:** 스크립트 투수(존 중앙, 구속 무작위)와 `TrainingEnvController`
- **2단계:** 구종 물리와 `PitcherAgent`·`PitcherRewardTracker`
- **3단계:** 아래 단계 5·6의 일부를 학습용으로 구현했다.
  - 수비 5명 이동·포구·송구와 공 소유
  - 1루 포스·태그·뜬공 아웃과 `RunnerSafe` 판정
  - `RunnerAgent`·`FielderAgent`(그룹)·`PlayOutcomeRewards`
  - 단계 5의 원래 계획(자동 수비 스크립트)은 수비 Agent와 검증용 스크립트 수비로 대신했다.

같은 날 후속 요청으로 남은 네 가지를 구현·검증했다.

- 구종 변화량을 MLB 우투수 평균에 보정
- 볼카운트와 타석 단위 에피소드
- 주자 여러 명(누상 주자 3명, 포스·태그·리터치·이닝 상황)
- 타자 출루·아웃 결과 보상

기준은 [상황 규칙](game-situation.md)이다. 남은 일: Python 학습 실행과 성능 확인, 포수·2루수·도루.

## 타자 Agent·센서 연결 (2026-09-24)

`com.unity.ml-agents` 4.0.3, `BatterAgent`, 16값 벡터 관측, 투구 전 자세·투구 중 스윙 행동, 보상/종료 연결을 추가했다. 2026-09-26 후속 요청으로 다음을 바꿨다.

- Agent를 타자 루트로 옮겼다.
- 공 정답 위치·속도를 관측에서 빼 벡터 관측을 10값으로 줄였다.
- 머리 높이 레이 센서 `BallEye`로만 공을 보게 했다.
- 0.38초 고정 스윙 휴리스틱을 중립 행동으로 바꿨다.

씬 설정과 검증 절차는 `batter-agent.md`를 따른다. 학습 YAML·Python trainer·학습 모델 및 성능 검증은 후속 단계다.

## 타자 보상 설계 초안 (2026-09-24)

사용자 요청의 여섯 보상 조건에 대한 초기 가중치·지급 시점·중복 방지·검증 예시를 `batter-reward-design.md`에 정하고, `BatterRewardTracker`와 `BatterRewardVerification`을 추가했다. 환경 결과를 바꾸지 않고 `PlayDirector` 이벤트를 읽어 한 투구의 보상 증분과 성분별 합계를 제공한다. Agent 연결은 위 최신 절에 기록했고 학습은 후속 단계다. 첫 학습은 고정 투구·고정 파워 조건으로 시작하고, 보상 성분을 관찰한 뒤 조건을 넓힌다.

## 최신 진행 — 볼/스트라이크 판정 (2026-09-24)

사용자 요청으로 투구별 볼/스트라이크 판정을 구현했다. 규칙 존(`StrikeZone`), 투구 위치 방식(기본 머신 목표, 무작위, 명령 지정), `PitchCall` 판정·이벤트·스냅샷, 시드 초기화, HUD 존 패널, 존/플레이트 표시 정렬 메뉴, `PitchCallVerification` 검증 메뉴를 추가했다. 설정의 표시 전용 `Strike Zone Half Width/Height`는 규칙 존 `Strike Zone Bottom/Top`으로 바꿨다(설정 에셋에 저장된 값이 없어 이전 값 손실은 없다). 검증 기록은 `verification.md` 12.7이다.

남은 작업: 볼카운트·삼진·볼넷(타석 단위 진행), 공을 보고 손·배트 위치를 조정하는 타자 동작, 몸에 맞는 공·파울팁. 원본 씬의 3D 존 틀은 사용자가 정렬 메뉴를 실행해야 규칙 존과 맞는다.

## 2026-09-23 타구 판정·물리 확장 완료 범위

직구/타구 공기력, 지면 반발·마찰·구름 저항, 외야 충돌 펜스, 첫 닿음/베이스 통과 기반 페어·파울, 홈런·인정 2루타 결과, 설정 시드로 재현 가능한 스윙 파워 배율을 구현했다. 원본 씬에 주자와 펜스를 저장했다. `BattedBallVerification`의 실제 Rigidbody 검증과 기존 주루·타격 회귀 검증을 통과했다. 아래의 이전 단계별 미구현 목록 중 파울·홈런·공기역학·펜스 항목은 이 기록으로 대체한다. 남은 단계는 수비·아웃/세이프·송구 및 홈런·인정 2루타의 자동 베이스 수여다.

## 최신 진행 — 주루 (2026-09-23)

사용자 요청으로 **단계 4 단일 주자 이동을 부분 구현**했다. 달리기는 환경이 제공하고, 타자주자는 1루 이후 진루/귀루만 판단한다. 판단 주체(에이전트)는 넣지 않았고, 나중에 `RequestRunnerDecision`에 연결할 수 있게 명령·스냅샷·이벤트 경계만 만들었다. 규칙은 `environment-spec.md` 최신 주루 절을 따른다.

| 경로 | 내용 |
| --- | --- |
| `Scripts/World/RunnerController.cs` | 신규. 경로 이동, 진루/귀루 적용, 베이스 밟음 시각 보간, `BaseReached` 사실 보고 |
| `Scripts/Core/SimulationContracts.cs` | `RunnerPhase`, `RunnerDecision`, `RunnerSnapshot`, `PitchEndReason.RunScored` 및 타구 판정 계약 |
| `Scripts/Core/PlayDirector.cs` | 선택 참조 `runner`, `RequestRunnerDecision`, `GetRunnerSnapshot`, `RunnerBaseReached`, 접촉 시 주자 전환, 득점 종료, 타구 판정 연계 |
| `Scripts/World/BatterController.cs` | `SetVisible` — 접촉 때 숨기고 초기화 때 다시 표시 |
| `Scripts/Settings/BaseballEnvironmentConfig.cs` | `Runner Speed` 7 m/s, `Runner Arrival Radius` 0.30 m와 검증 |
| `Scripts/Input/ManualPlayController.cs`, `Scripts/Presentation/DebugPresenter.cs` | F 진루 / B 귀루, HUD 주자 표시 |
| `Scripts/Editor/RunnerSceneSetup.cs`, `BatterSceneSetup.cs`, `BaseballPlaygroundBuilder.cs` | 씬에 `BatterRunner` 추가·Director 연결 메뉴, 빌더에서 호출 |
| `Scripts/Editor/RunnerVerification.cs` | 신규. 일시 정지한 Play Mode용 주루 검증 메뉴 |

**원본 씬에 주자와 외야 펜스를 저장했다.** 주자를 다시 만들 필요 없이 `BaseballPlayground`를 열어 사용할 수 있다.

남은 작업: 수비·포구·송구(단계 5), 아웃/세이프·포스 판정(단계 6; 파울 판정은 2026-09-23 구현), 홈런·인정 2루타의 진루권 수여, 판단 에이전트와 그 관측·보상 정의(학습 단계). 기존 단계 4 계획의 "1루 도착 시 `SafeAtFirst` 종료"는 판단을 받기 위해 적용하지 않았고, 수비가 생기면 다시 정한다.

## 최신 진행 — 2026-09-18

후속 요청으로 다음 우선순위를 주루보다 **타자 학습용 제어·평가**로 변경했다. 몸 위치와 상대 배트 위치를 독립 명령으로 제공하고, 실제 스윙 각도·타이밍을 포함해 네 항목의 원시 오차/점수를 제공한다. 환경 측 구현과 반복 검증을 완료한 뒤 ML-Agents 어댑터/에피소드 초기 분포/보상 정의를 후속 작업으로 둔다. 자세의 기준과 API는 `batting-evaluation.md`를 따른다. 기존 단계 4~7은 보류 상태이며 삭제하지 않는다.

사용자 요청으로 단계 3 타자·스윙·접촉·타구 계산을 구현했다. 기존 씬에 타자를 연결하고 P/Space/방향키/R 조작, 스크립트용 SwingCommand, 타격 기록 HUD를 추가했다. Unity 실제 물리의 정상/이른/늦은 스윙, 좌우 각도, 10회 초기화 검증을 실행했다(verification.md 12.3). 직접 키 입력과 Game 뷰 외형 확인은 남아 있다.

아래의 단계 3~7 미구현 설명 중 단계 3은 이 기록으로 대체한다. 단계 4~7, 주루·수비·야구 결과 판정은 계속 미구현이다. 현 단계 타구 관찰은 `BattedBallInFlight`로 구분하고 장외/12초로 종료한다. 이후 작업은 사용자가 요청할 때 단일 주자 이동부터 진행한다.

## 1. 계획 상태와 진행 원칙

이 문서는 앞으로 수행할 작업 계획이다. 2026-09-11 기준으로 **단계 1은 구현 완료(일부 검증)**이었고, 2026-09-12에 **단계 2는 피칭머신 범위로 부분 구현(일부 검증)**했다. **단계 3~7은 여전히 미구현·미검증**이다. 각 단계의 현재 상태는 아래 "상태" 줄에 적고, 실제 실행한 검증과 실행하지 못한 검증은 `verification.md`에서 구분한다.

각 단계는 Unity Editor에서 독립적으로 실행해 눈으로 확인할 수 있는 결과를 남긴다. 앞 단계의 완료 조건을 만족하기 전에는 뒤 단계의 규칙을 한꺼번에 추가하지 않는다. 구현 중 명세와 다른 선택이 필요하면 코드만 바꾸지 말고 `environment-spec.md`, `architecture.md`, `verification.md`를 함께 갱신한다.

필수 단계 순서는 다음과 같다.

1. 야구장과 기본 배치
2. 공 물리와 투구
3. 타격
4. 단일 주자 이동
5. 포구와 송구
6. 플레이 판정과 초기화
7. 수동·자동 검증 통합

## 2. 단계 1 — 야구장과 기본 배치

**상태:** 구현 완료, 일부 검증 (2026-09-11)

**목적:** 좌표와 거리를 눈으로 확인할 수 있고 이후 모든 동작이 공유할 기준 Transform을 만든다.

**선행 조건**

- Unity `6000.5.2f1`로 프로젝트를 열 수 있다.
- Console에 작업 전 컴파일 오류가 없는지 확인한다.
- `environment-spec.md`의 좌표계와 주요 위치를 읽는다.

**작은 작업 단위**

1. `Assets/BaseballSimulation/` 최소 폴더와 `BaseballPlayground.unity`를 만든다.
2. 기존 `SampleScene`의 카메라, Directional Light, Global Volume 구성을 출발점으로 별도 씬에 보존한다.
3. 기본 도형과 단색 URP 머티리얼로 지면, 내야/외야, 파울 영역을 구분한다.
4. 홈, 1·2·3루, 투수 위치, 투구 시작점/목표점 Transform을 명세 좌표에 둔다.
5. 파울선과 수평 `110 m` 플레이 경계를 표시한다.
6. `FieldLayout`과 기본 설정 데이터에는 위치 참조와 단위만 먼저 연결한다.
7. 축, 베이스 경로, 주요 거리와 경계를 Scene 뷰에서 표시한다.

**산출물**

- `BaseballPlayground.unity`
- 필드 기본 머티리얼
- `FieldLayout` 및 `BaseballEnvironmentConfig`의 최소 구현과 기본 설정 에셋
- 이후 단계가 참조할 명시적 Spawn/Base Transform

**검증 방법**

- 홈을 원점으로 확인하고 1·3루 X 부호, 2루와 투수 위치의 +Z 방향을 확인한다.
- 홈-1루/3루 거리 약 `27.43 m`, 홈-투수 거리 `18.44 m`를 Gizmo 또는 측정 표시로 확인한다.
- 카메라에서 공 배치 예정점, 홈, 1루, 두 수비 위치를 식별할 수 있는지 확인한다.
- 씬 저장 후 닫았다 다시 열어 참조 누락과 Console 오류가 없는지 확인한다.

**완료 조건**

- 좌표·단위·파울 방향이 명세와 일치한다.
- 모든 기준점이 한 `FieldLayout`에서 참조 가능하다.
- 외부 에셋 없이 필드 영역과 주요 위치를 구분할 수 있다.
- 기존 `SampleScene`과 그 참조를 훼손하지 않는다.

### 2.1 실제 구현 결과 (2026-09-11)

만든 파일은 다음과 같다.

| 경로 | 내용 |
| --- | --- |
| `Assets/BaseballSimulation/Scripts/Core/SimulationContracts.cs` | `BaseId` 열거형만. 나머지 계약 값은 사용하는 단계에서 추가한다. |
| `Assets/BaseballSimulation/Scripts/Settings/BaseballEnvironmentConfig.cs` | 필드 좌표, 경기 경계, Gizmo 토글과 `TryValidate` |
| `Assets/BaseballSimulation/Scripts/World/FieldLayout.cs` | 기준 Transform 참조, 주루 경로, 페어/경계 질의, Gizmo |
| `Assets/BaseballSimulation/Scripts/Editor/BaseballPlaygroundBuilder.cs` | 씬·머티리얼·설정 에셋 생성과 검증 메뉴 |
| `Assets/BaseballSimulation/Scenes/BaseballPlayground.unity` | 위 스크립트로 Editor가 생성한 씬 |
| `Assets/BaseballSimulation/Materials/*.mat` | URP Lit 단색 머티리얼 8개(필드 6, 관중석 2) |
| `Assets/BaseballSimulation/Config/DefaultBaseballEnvironment.asset` | 기본 설정 에셋 |

씬은 손으로 YAML을 쓰지 않고 `Tools > Baseball Simulation > Build Playground Scene` 메뉴가 Unity Editor API로 만든다. 같은 메뉴를 다시 실행하면 규격대로 다시 만들고, `Tools > Baseball Simulation > Validate Playground Scene`은 기준점 참조와 실측 거리를 Console에 출력한다.

씬 구성은 `BaseballEnvironment/Field` 아래에 `Ground`, `FairTerritory`, `FoulTerritory`, `Infield`, `FoulVisuals`, `Home`/`First`/`Second`/`Third`/`PitcherPlate`, `PitchOrigin`, `PitchTarget`, `PlayBoundary`를 두고, `SampleScene`에서 가져온 `Main Camera`, `Directional Light`, `Global Volume`을 최상위에 둔다. `FieldLayout`은 `Field`에 붙는다. GameObject는 필드 65개와 관중석 132개를 합쳐 197개다.

페어 영역은 경계 반경 `110 m` 원판(`FairTerritory`) 위에 페어가 아닌 세 방향을 지면 색 사각 판(`FoulTerritory`)으로 덮어 만든다. 원판과 쐐기 모두 홈을 기준으로 하므로 보이는 페어 영역이 `z >= abs(x)` 조건과 원형 경계에 그대로 맞는다. `Infield`는 흙 다이아몬드(한 변 `27.4357 m`) 위에 안쪽 잔디(한 변 `21.0357 m`)를 얹어 폭 `3.2 m`의 베이스 패스를 남기고, 투수판에 반경 `2.74 m` 마운드, 홈에 반경 `3.96 m` 원을 둔다.

`Stands`는 사용자 요청으로 추가한 표시 전용 관중석이며 `Field`가 아닌 형제 그룹이다. 경계 밖 `114 m`부터 단 깊이 `8 m`, 단 높이 `3 m`로 4단을 두르고 각 단을 32조각으로 나눈다(총 128개). 가장 바깥 단은 외벽 역할이라 색을 구분한다. Collider가 없고 경계 안으로 들어오지 않아 판정에 관여하지 않는다.

베이스 표식은 **빈 GameObject(기준점) + 자식 `Visual`(표시용 판)** 구조다. 기준점은 명세 좌표에 정확히 두고, 보이는 판만 지면 위로 몇 cm 띄운다. 이렇게 하지 않으면 `FieldLayout`이 보고하는 베이스 좌표에 표시용 높이가 섞인다.

물리 지면은 `Ground`의 MeshCollider 하나뿐이고, 나머지 표시용 판과 경계 표식에서는 Collider를 제거했다.

**검증 결과는 `verification.md` 12장에 기록했다.**

## 3. 단계 2 — 공 물리와 직구

**상태:** 부분 구현 — 피칭머신(직구 하나, 중앙 통과, 재투구, 초기화) (2026-09-12). 실행 기록은 `verification.md` 12.2.

**목적:** 설정 가능한 시작점·목표·속도로 공을 한 번 던지고 위치·속도·정지·장외 상태를 확인한다.

**선행 조건**

- 단계 1 완료
- 지면 Collider, 투구 시작점, 목표점, 플레이 경계가 존재

**작은 작업 단위**

1. Sphere 기반 공과 Rigidbody/Collider를 만들고 반지름, 질량, 연속 충돌 검사를 설정한다.
2. `BallController`에 자유 이동, 소유/대기, 지면 접촉, 정지 상태를 구현한다.
3. `SimulationContracts`에 최소 투구 명령·공 상태·환경 이벤트 값을 정의한다.
4. `PlayDirector`의 `Ready → PitchInFlight` 최소 흐름과 `ThrowPitch` 명령 검사를 만든다.
5. 시작점에서 중력 낙하를 보정한 `36 m/s` 초기 속도를 계산해 목표(스트라이크존 중앙)를 통과하도록 한 번 적용한다.
6. 위치, 속도, 지면 접촉, 경계 이탈, 정지 시간 정보를 디버그 표시한다.
7. 임시 Inspector 버튼이나 한 개의 테스트 키로 투구와 초기 위치 복귀를 확인한다. 이 임시 진입점도 공 API를 직접 우회하지 않는다.

**산출물**

- 직구 가능한 BallController와 PlayDirector 최소 흐름
- 공 상태 스냅샷 및 투구/지면/경계 이벤트
- 공 속도 벡터와 상태 표시

**검증 방법**

- 정상 투구를 실행해 공이 홈 방향으로 이동하고 중력으로 낙하하는지 본다.
- 속도와 목표를 바꿔 궤적 변화가 반영되는지 본다.
- 공을 지면에 떨어뜨려 충돌, 정지 기준과 `DeadBall` 후보 사실을 확인한다.
- 경계 밖 시작/속도를 사용해 이탈 사실이 한 번만 기록되는지 확인한다.

**완료 조건**

- 같은 명령에 투구가 중복 실행되지 않는다.
- 공 위치·속도·소유/접촉 상태를 외부에서 읽을 수 있다.
- 정지 또는 장외 조건에서 무한히 진행하지 않는다.
- 아직 변화구, 스핀, 학습 입력을 추가하지 않는다.

### 3.1 실제 구현 결과 — 피칭머신 (2026-09-12)

사용자가 이 단계 전체가 아니라 **피칭머신(직구 하나, 중앙 통과, 재투구, 초기화)**을 명시적으로 요청해 그 범위로 구현했다. 공 소유권(포구·송구에 의한 획득/해제), 정지 기준 기반 `DeadBall`, 가변 `PitchCommand`(속력·목표를 명령 인자로 받는 일반화)는 이번에 포함하지 않았다.

만든/바꾼 파일은 다음과 같다.

| 경로 | 내용 |
| --- | --- |
| `Assets/BaseballSimulation/Scripts/Core/SimulationContracts.cs` | `PlayState`(Ready/PitchInFlight/Ended), `PitchEndReason`, 읽기 전용 `PitchSnapshot` 추가 |
| `Assets/BaseballSimulation/Scripts/Core/PlayDirector.cs` | 신규. 중력 보정 탄도 계산(`TryComputeLaunchVelocity`), 목표 평면 교차 계산(`TryComputePlaneCrossing`), 투구/초기화 명령, 자동 반복 |
| `Assets/BaseballSimulation/Scripts/World/BallController.cs` | 신규. Rigidbody 발사·초기화, 질량·연속 충돌 검사(CCD)·보간 설정 |
| `Assets/BaseballSimulation/Scripts/Input/ManualPlayController.cs` | 신규. P=투구, R=초기화(새 Input System `Keyboard.current`) |
| `Assets/BaseballSimulation/Scripts/Presentation/DebugPresenter.cs` | 신규. OnGUI HUD(상태, 공 속력, 통과 오차, 조작 안내) |
| `Assets/BaseballSimulation/Scripts/Settings/BaseballEnvironmentConfig.cs` | 공 반지름/질량, 투구 속력·제한 시간, 목표 뒤쪽 여유 거리, 스트라이크존 절반 폭/높이와 검증 추가 |
| `Assets/BaseballSimulation/Scripts/Editor/BaseballPlaygroundBuilder.cs` | 피칭머신 외형(받침대/본체/발사구), 스트라이크존 표시, 공, `Actors`/`Systems` 그룹과 컴포넌트 배선 추가 |

피칭머신 외형은 `PitchOrigin`의 자식으로 Cube(받침대) + Cylinder 2개(본체·발사구)를 두고 모든 부품에서 Collider를 제거했다. 발사구 앞면이 `PitchOrigin`과 정확히 겹치고 본체는 홈 반대 방향으로 뻗어 있어 공 발사를 가리지 않는다. 스트라이크존 표시는 `PitchTarget`의 자식으로 테두리 막대 4개와 중앙 표시 1개를 두며 역시 Collider가 없다.

`PlayDirector`는 환경 명세 6장 상태表의 부분집합(`Ready`/`PitchInFlight`/`Ended`)만 쓴다. 목표 평면 통과는 이전·현재 고정 시간 단계 위치를 잇는 선분과 평면의 교차점으로 계산해(`TryComputePlaneCrossing`) 빠른 공도 놓치지 않는다. 초기 속도는 목표 방향으로 속도만 적용하는 대신 `TryComputeLaunchVelocity`로 중력 낙하를 보정한 값을 계산하며, 같은 조건에 두 해(낮고 빠른 해/높은 포물선 해)가 있을 때 항상 더 평평한 해를 골라 "빠르고 낮은 탄도" 요구를 만족한다.

**검증 결과는 `verification.md` 12.2에 기록했다.**

## 4. 단계 3 — 타격

**상태:** 계획됨

**목적:** 스윙 시각과 두 각도가 접촉 여부 및 타구 방향·속도에 영향을 주는 단순 모델을 완성한다.

**선행 조건**

- 단계 2 완료
- 공의 고정 시간 단계 위치/속도와 홈 통과를 확인할 수 있음

**작은 작업 단위**

1. Capsule 등으로 타자와 배트를 배치하고 스윙 전/후 자세를 정한다.
2. `SwingCommand`의 시작 시각, 좌우 각, 발사각과 유효 범위를 정의한다.
3. BatterController에서 고정 시간 기준 `0.25 s` 단순 스윙을 구현한다.
4. 이전·현재 배트 구간을 이용한 휩쓸림 접촉 검사를 만든다.
5. 타이밍 품질과 각도로 `8–32 m/s` 타구 속도 벡터를 계산해 공에 한 번만 적용한다.
6. 홈 뒤 미타격 판정면을 지나면 `PitchMissed` 사실을 보고한다.
7. 첫 지면 접촉/경계 위치로 간이 페어·파울 사실을 보고한다.

**산출물**

- 타자/배트 기본 표현
- 스윙 명령과 접촉 검출
- 단순 타구 계산과 접촉·미타격·파울 이벤트
- 스윙 시간/각도/접촉 품질 디버그 표시

**검증 방법**

- 충분히 이른 스윙과 늦은 스윙이 헛스윙으로 끝나는지 본다.
- 중심 시각에서 접촉이 한 번만 발생하는지 본다.
- 좌우 각 부호를 바꿔 타구가 1루/3루 쪽으로 나뉘는지 본다.
- 발사각 0° 부근과 큰 각도에서 땅볼/뜬공 궤적이 구분되는지 본다.
- 파울 각도에서 첫 접촉 지점 기준 파울 사실이 기록되는지 본다.

**완료 조건**

- 동일한 타이밍·각도 입력은 같은 계산 결과 벡터를 만든다.
- 빠른 공이 배트 사이를 지나도 휩쓸림 검사로 의도한 접촉을 검출한다.
- 배트 Rigidbody 정밀 충격이나 애니메이션이 없어도 타격 결과를 조절 가능하다.
- 접촉과 미타격이 한 플레이에서 동시에 최종 결과가 되지 않는다.

## 5. 단계 4 — 단일 주자 이동

**상태:** 부분 구현 (2026-09-23). 이동·진루/귀루 판단·득점 종료·초기화 구현. 1루 도착 `SafeAtFirst` 종료는 적용하지 않았다(맨 위 최신 진행 참고).

**목적:** 유효 타격 후 타자주자 1명이 베이스 순서에 따라 이동하고 목표·도착 상태를 제공한다.

**선행 조건**

- 단계 3 완료
- 홈과 각 베이스 기준점 연결 완료

**작은 작업 단위**

1. Capsule 기반 비활성 주자를 홈 근처 초기 위치에 둔다.
2. 접촉 이벤트에서 타자를 주자로 전환하고 RunnerController를 활성화한다.
3. 홈 → 1루 → 2루 → 3루 → 홈 경로와 다음/최종 목표를 구현한다.
4. 고정 시간 단계에서 설정 속도로 이동하고 도착 반경 진입을 한 번만 보고한다.
5. 기본 1루 종료 모드와 홈까지 진행하는 득점 검증 모드를 설정으로 구분한다.
6. 현재 위치, 다음 베이스, 최종 목표, 도착 상태를 스냅샷과 Gizmo에 추가한다.

**산출물**

- RunnerController와 단일 주자 상태
- 베이스 경로 및 `BaseReached` 이벤트
- 1루 목표/홈 목표 두 검증 설정

**검증 방법**

- 타격 전 주자가 움직이지 않는지 확인한다.
- 1루 목표에서 1루 도착 뒤 정확히 멈추는지 확인한다.
- 홈 목표에서 중간 베이스 순서를 건너뛰지 않고 한 바퀴 도는지 확인한다.
- 이동 속도와 도착 반경을 바꿔도 중복 도착이나 진동이 없는지 확인한다.

**완료 조건**

- 한 명만 활성화되며 위치·목표·도착 상태가 일치한다.
- 경로 역행이나 베이스 건너뛰기가 없다.
- 초기화 시 홈 초기 위치와 비활성 상태로 돌아간다.
- 복수 주자, 도루, 리드, 태그업을 추가하지 않는다.

## 6. 단계 5 — 포구와 송구

**상태:** 계획됨

**목적:** 최소 수비가 공을 따라가 포구하고 1루 수비수에게 송구하며 공 소유권을 일관되게 이전한다.

**선행 조건**

- 단계 4 완료
- 공의 지면 접촉 이력과 소유 상태가 조회 가능

**작은 작업 단위**

1. 타구 처리 수비수와 1루 수비수를 Capsule로 배치한다.
2. 현재 공 위치 또는 중력 기반 간이 낙하지점 계산을 이동 목표로 사용한다.
3. 고정 시간 단계에서 수비수를 이동시키고 포구 반경·높이 조건을 표시한다.
4. 지면 접촉 전/후 포구를 구분해 `BallCaught` 사실을 보고한다.
5. BallController에 원자적 `Acquire`/`Release`와 소유권 변경 이벤트를 구현한다.
6. 소유 수비수에서 1루 수비수 수신점으로 기본 `25 m/s` 직선 송구를 구현한다.
7. 수신 반경에서 소유권을 1루 수비수로 한 번만 이전한다.

**산출물**

- 두 수비수의 이동·포구·송구
- 공 소유 상태와 변경 이벤트
- 낙하지점, 포구/수신 반경, 송구 대상 디버그 표시

**검증 방법**

- 뜬공과 땅볼 각각에서 수비수가 공으로 이동하는지 확인한다.
- 공중 포구가 첫 지면 접촉 전으로 기록되는지 확인한다.
- 송구 직후 원래 수비수 소유가 해제되고, 수신 전에는 소유자가 없는지 확인한다.
- 수신 순간 1루 수비수만 소유자가 되는지 확인한다.
- 포구/수신 반경 바로 안팎의 값을 사용해 경계 조건을 확인한다.

**완료 조건**

- 동시에 두 명이 공을 소유할 수 없다.
- 포구와 수신 이벤트가 한 번씩만 발생한다.
- 송구 대상과 공 속도를 조회할 수 있다.
- 자동 이동은 단순 검증 규칙이며 학습 정책으로 표현하지 않는다.

## 7. 단계 6 — 플레이 판정과 초기화

**상태:** 계획됨

**목적:** 분산된 물리 사실을 한 결과로 중재하고 어떤 시점에서도 잔존 상태 없이 초기화한다.

**선행 조건**

- 단계 2~5의 사건과 상태가 Director로 보고됨
- 환경 명세의 상태 전이와 동시 사건 우선순위 합의

**작은 작업 단위**

1. `Ready`, `PitchInFlight`, `RunnerDefense`, `Ended`, `Resetting` 상태 전이를 완성한다.
2. 사건에 시뮬레이션 시각, 고정 틱, 순번을 부여하고 틱 단위 버퍼를 만든다.
3. `Miss`, `Foul`, `FlyOut`, `SafeAtFirst`, `ForceOutAtFirst`, `RunScored`, `OutOfPlay`, `DeadBall`, `Timeout`, `InvalidState` 판정을 구현한다.
4. 같은 틱에 여러 사건이 들어오면 환경 명세 우선순위로 하나만 확정한다.
5. 1루 수비수의 공 소유+베이스 점유와 주자 도착 시각을 비교한다.
6. 모든 객체의 초기 상태를 복원하는 초기화 순서와 `ResetState` 계약을 구현한다.
7. 플레이 도중, 종료 후, 연속 초기화 요청을 같은 경로로 처리한다.

**산출물**

- 완전한 PlayDirector 상태 머신과 결과 중재
- 결과/이벤트 이력 표시
- 공·선수·주자·배트·소유권·타이머의 통합 초기화

**검증 방법**

- 각 결과를 만드는 제어된 조건을 한 번씩 실행한다.
- 1루 도착과 포스를 같은 틱에 만들고 포스 우선이 한 번만 적용되는지 확인한다.
- 공중 포구와 파울 후보가 겹칠 때 FlyOut 규칙을 확인한다.
- 플레이 도중 초기화, 종료 직후 초기화, 연속 10회 초기화를 실행한다.
- 초기화 전후 스냅샷을 비교해 속도, 소유권, 플래그, 타이머, 결과, 큐가 복원됐는지 확인한다.

**완료 조건**

- 한 플레이에 최종 결과가 정확히 하나다.
- `Ended` 후 명령이 상태를 바꾸지 않는다.
- 초기화 뒤 첫 실행과 동일한 초기 조건에서 다시 시작할 수 있다.
- Console 예외와 이전 플레이 이벤트 재발행이 없다.

## 8. 단계 7 — 수동·스크립트 검증 통합

**상태:** 계획됨

**목적:** 사람과 결정적 시간표가 같은 환경 API로 한 플레이를 실행하고 결과를 확인하게 한다.

**선행 조건**

- 단계 6 완료
- 모든 환경 동작이 Director 명령과 스냅샷/이벤트를 통해 접근 가능

**작은 작업 단위**

1. ManualPlayController에서 투구, 스윙, 각도 조절, 주자 목표, 송구, 일시 정지, 초기화를 매핑한다.
2. ScriptedPlayController에서 시드, 명령 시각, 투구/스윙/수비 값을 가진 한 플레이 시간표를 실행한다.
3. 두 입력원 중 하나만 활성화되게 하고 현재 모드를 HUD에 표시한다.
4. 정상 투구, 헛스윙, 페어 타격, 파울, 뜬공 포구, 1루 세이프, 1루 포스 아웃, 득점, 장외, 시간 초과 시나리오를 설정한다.
5. 상태, 공 속도, 소유자, 주자 목표, 결과, 시간, 시드를 한 화면에 표시한다.
6. `verification.md` 절차를 실제 실행하고 결과와 실행 환경을 기록한다.

**산출물**

- 수동 조작 모드
- 재현 가능한 스크립트 검증 모드
- 통합 HUD/Gizmo
- 실제 검증 기록

**검증 방법**

- 같은 값의 수동/스크립트 명령이 같은 상태 전이와 계산된 타구 벡터를 만드는지 비교한다.
- 스크립트 시나리오를 같은 시드로 여러 번 실행해 논리 결과가 안정적인지 본다.
- 속도, 각도, 선수 시작 위치를 하나씩 바꿔 범위 안에서 정상 종료하는지 확인한다.
- 전체 시나리오 뒤 Console 오류와 초기화 잔존 상태를 확인한다.

**완료 조건**

- 학습 모델 없이 한 플레이의 전 과정을 두 입력 모드로 실행할 수 있다.
- 모든 필수 결과 시나리오가 절차와 기대 결과에 따라 실제 확인되었다.
- 입력 컨트롤러에 규칙·Rigidbody 직접 변경이 중복되어 있지 않다.
- 실행한 검증과 실행하지 못한 검증이 문서에서 구분된다.

## 9. 필수 기능과 후속 확장

| 이번 단계 필수 | 후속 단계로 분리 |
| --- | --- |
| 단순 필드, 직구, 단순 혼합 타격 | 변화구, 스핀, 공기역학, 정밀 배트 물리 |
| 타자주자 1명과 순차 베이스 경로 | 복수 주자, 리드, 도루, 태그업 |
| 타구 처리 수비수+1루 수비수 | 전체 수비 포지션, 복잡한 커버·중계 |
| 공중 포구, 1루 세이프/포스, 득점 시나리오 | 태그 아웃, 병살, 인필드 플라이, 전체 야구 규칙 |
| Miss/Foul/OutOfPlay/DeadBall/Timeout | 볼카운트, 삼진, 볼넷, 홈런, 이닝/팀 운영 |
| 수동·스크립트 입력과 디버그 HUD | 운영 UI, 멀티플레이, 관중·중계 |
| 환경 명령·스냅샷·이벤트·초기화, 타자 Agent·보상·관측·행동 | 주루/수비 Agent, 학습 설정·실행 |

## 10. 다음 구현 작업

단계 1(`BaseballPlayground.unity`와 FieldLayout 기준점)은 끝났다. 단계 2는 사용자가 명시적으로 요청한 **피칭머신(직구 하나, 중앙 통과, 재투구, 초기화)** 범위로 부분 구현했다(12.2 기록).

단계 2에서 남은 것(사용자가 명시적으로 다음 단계로 확장하기 전에는 추가하지 않는다):

- 공 소유권(포구·송구에 의한 획득/해제)과 정지 기준 기반 `DeadBall`
- 속력·목표를 인자로 받는 일반화된 `PitchCommand`(현재는 항상 설정값 그대로 던진다)
- `RunnerDefense`/`Resetting` 상태와 관련 전이

다음 구현 요청에서 가장 먼저 할 일은 **단계 3의 타격**이다. 타자/배트 배치, `SwingCommand`, 휩쓸림 접촉 검사, 접촉 시 단순 타구 계산까지만 만든다. 이 작업에 주루, 수비, 결과 판정, ML-Agents를 섞지 않는다.

단계 3을 시작하기 전에 Unity Editor에서 단계 1의 미실행 검증 항목(`verification.md` 12.1)과 피칭머신의 미실행 검증 항목(`verification.md` 12.2)을 먼저 확인한다.
