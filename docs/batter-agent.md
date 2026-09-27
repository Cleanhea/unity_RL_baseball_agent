# 타자 ML-Agents 연결

2026-09-24 요청으로 `BatterAgent`를 학습 입력원으로 연결했다. 2026-09-26 볼카운트 추가로 에피소드는 **한 타석**이다([상황 규칙](game-situation.md)). Unity 6000.5.2f1에서 ML-Agents `com.unity.ml-agents` 4.0.3을 사용한다. `PlayDirector`는 경기 규칙과 물리를 소유하고, Agent는 기존 명령 경계를 호출한다. 2026-09-26 요청으로 공은 타자 머리의 **레이 센서(`BallEye`)로만** 본다. `CollectObservations(VectorSensor)`에는 타자 자신의 상태와 볼카운트·아웃·주자 상황 16값만 넣고, 공의 정답 위치·속도는 넣지 않는다. 카메라 센서는 사용하지 않는다. 학습 설정은 `Training/config/`에 있고, 학습된 모델은 아직 없다.

## 씬 연결

2026-09-26부터 타자 Agent는 **학습 단계 씬**(`Scenes/Training/Stage1~3`)에만 있다. `BaseballPlayground`는 Agent가 없는 수동 조작·검증용 틀 씬이다. 단계 씬은 `Tools > Baseball Simulation > Training > Build Stage N Scene`으로 만든다([학습 단계](training-curriculum.md)).

단계 씬에서 `PlayDirector`가 참조하는 타자 루트(`Actors/Batter`, `BatterController`와 같은 오브젝트)에 `BatterAgent`와 `BehaviorParameters`를 둔다.

- **배치 이유:** 같은 날 별도 `Systems/BatterAgent`에서 옮겼다. `Use Child Sensors`는 Agent 오브젝트의 자식 센서만 모은다. 타자 몸에 단 센서가 타자와 함께 움직이며 수집되게 하려는 것이다. 투수·수비·주자 Agent도 각 선수 루트에 둔다.
- **명령 경계:** 같은 오브젝트에 있어도 Agent는 `BatterController`를 직접 호출하지 않고 `PlayDirector` 명령만 사용한다.
- **Behavior Parameters:** `Behavior Name = BaseballBatter`, `Behavior Type = Default`, `Team Id = 0`, `Use Child Sensors = true`, `Vector Observation Size = 16`, `Stacked Vectors = 1`, 연속 행동 7개, 이산 행동 분기 `[2]`, `Max Step = 0`
- **결정 요청:** Decision Requester는 두지 않는다. 같은 씬의 `TrainingEnvController`가 필요한 때만 결정을 요청한다. 투구 전 자세 1번, 공이 날아오는 동안 스윙할 때까지 매 고정 단계다. 투구 요청·초기화·수동 입력 끄기도 컨트롤러가 한다.
- **평가 타석 (2026-09-27, 2·3단계):** 새 타석의 약 10%는 컨트롤러가 타자의 Behavior Type을 `InferenceOnly`로, Model을 `Models/BenchmarkBatter_Stage1.onnx`로 바꿔 둔다. 이 타석의 타자 데이터는 학습기로 가지 않는다. 앞 타석 에피소드가 닫힌 뒤 바꾸고 타석이 끝나면 되돌린다. 약 10%는 기준 스크립트 투수가 던지며 타자는 평소처럼 학습한다. [고정 상대 평가](training-curriculum.md#고정-상대-평가-2026-09-27) 참고.

`Tools > Baseball Simulation > Add Batter ML-Agent To Current Scene` 메뉴는 단계 씬 빌더가 쓰며, 직접 실행해도 된다. 동작은 다음과 같다.

- 이전 전용 오브젝트(Agent 컴포넌트만 있고 자식이 없는 경우)가 있으면 지우고 타자 루트에 다시 붙인다.
- 위 Behavior Parameters 값은 실행할 때마다 다시 맞춘다. 관측 크기가 다른 씬도 메뉴 한 번으로 현재 계약(16)이 된다. 남아 있는 Decision Requester는 지운다.
- 프로젝트 태그 `Ball`을 등록하고 저장하며, 공 오브젝트에 이 태그를 붙인다.
- 타자 루트에 레이 센서가 없을 때만 `BallEye`를 만든다. 이미 있으면 Inspector에서 조정한 값을 건드리지 않는다.
- `Build Playground Scene`으로 틀 씬을 다시 만든 뒤에는 단계 씬 메뉴를 다시 실행한다. 공 태그도 그때 다시 붙는다.

## 공을 보는 레이 센서 `BallEye`

타자 루트의 자식 `BallEye`에 `RayPerceptionSensorComponent3D`를 둔다. 위치는 `Head`와 같은 머리 높이다. 레이 부채꼴은 눈의 로컬 XZ 평면에 퍼지고, 이 평면이 **눈·발사 지점(`PitchOrigin`)·투구 목표(`PitchTarget`)를 모두 지나도록** 기울어 있다. 투구 경로가 거의 이 평면 위에 있으므로 한 부채꼴로 발사부터 홈 통과까지 공을 따라간다. 부채꼴 중앙은 발사 지점과 목표 방향의 가운데이고, 반각은 두 방향 사이 각의 절반에 12°를 더한다. 타자가 자세 명령으로 움직이면 눈도 함께 움직이지만 방향은 바꾸지 않는다.

| 설정 | 기본값 | 이유 |
| --- | --- | --- |
| 감지 태그 | `Ball` | 공만 태그로 구분한다. 다른 충돌체에 맞은 레이는 태그 없는 거리 값만 준다 |
| 한쪽 레이 수 | 25 (총 51개) | 발사 지점 거리(약 18 m)에서 레이 간격 약 0.7 m |
| 구체 캐스트 반지름 | 0.25 m | 공(반지름 0.037 m)이 레이 사이로 빠지는 것을 줄인다 |
| 레이 길이 | 눈–발사 지점 거리 + 3 m | |
| 관측 스택 | 3 | 연속 세 결정(0.06 s)을 쌓아 공의 이동 방향·속도를 알 수 있게 한다 |

레이 한 개는 `[Ball 태그 여부, 태그 없음, 맞은 거리 비율]` 3값이다. 센서 관측은 51 × 3 = 153값이고 스택 3으로 459값이다. 이 값은 벡터 관측 크기와 별개다.

한계: 목표가 부채꼴 평면에서 반지름보다 멀리 벗어나면 공을 놓친다. 무작위 투구 위치(`PitchLocationMode` 분산)를 크게 하거나 타자 자세 오프셋이 크면 이런 일이 생긴다. 먼 거리에서는 레이 간격이 넓어 일부 결정에서 공이 보이지 않을 수 있다. 조정은 `BallEye`의 Inspector 값으로 한다. 레이 수나 태그를 바꾸면 관측 크기가 바뀌어 기존 모델과 호환되지 않는다.

## 벡터 관측 16개

아래 순서는 학습 모델의 입력 계약이다. 각 항목은 표의 값으로 나눠 `[-1, 1]`로 제한하고, 시간은 `[0, 1]`로 제한한다. 공 위치·속도, 타구 결과, 보상은 입력하지 않는다.

- 2026-09-26: 처음 16값 계약에서 공 위치·속도를 빼 10값이 됐다.
- 같은 날 볼카운트·아웃·주자 6값(10–15)을 더해 다시 16값이 됐다.

| 인덱스 | 값 | 정규화 |
| --- | --- | --- |
| 0–1 | 자세 설정 가능, 투구 비행 중 플래그 | 0 또는 1 |
| 2 | 투구 경과 시간 | 2 s |
| 3–4 | 타자 기준 자세로부터 x/z 오프셋 | 설정의 `Stance Offset Limit` |
| 5–7 | 몸 기준 배트 손잡이 x/y/z 오프셋 | 설정의 `Grip Offset Limits` 각 축 |
| 8–9 | 스윙 시작, 공 접촉 플래그 | 0 또는 1 |
| 10–12 | 볼, 스트라이크, 아웃 | 3, 2, 2로 나눔 |
| 13–15 | 1·2·3루 주자 있음 | 0 또는 1 |

## 행동 7 + 1

연속 행동은 각각 `[-1, 1]`이고 비유한 값은 0으로 처리한다. **투구 전 첫 결정**에서 연속 0–1은 타자 자세 x/z, 2–4는 손잡이 x/y/z를 설정 범위에 곱해 `RequestBatterSetup`에 전달한다. 투구는 그다음 컨트롤러가 요청한다(1단계 스크립트 투수, 2단계부터 투수 Agent). 이후 자세 행동은 무시한다. **투구 중 결정**에서 이산 분기 0은 대기, 1은 스윙이다. 첫 1일 때만 연속 5를 좌우각 `[-45°,45°]`, 연속 6을 상향각 `[-30°,50°]`로 바꿔 `RequestSwing`에 전달한다. 스윙 후 행동은 무시한다. 손잡이 위치는 투구 중 조정할 수 없는 현재 환경 제약을 따른다.

컨트롤러는 매 투구 `PlayReset` 직후 `BatterAgent.BeginPitch()`를 호출하고, 이때 `BatterRewardTracker.BeginEpisode`로 그 투구의 보상 수집을 시작한다. 보상 증분은 `AddReward`에 바로 더한다.

타석이 끝나면(볼넷·삼진·인플레이) 컨트롤러가 `EndPlateAppearance(결과 보상)`을 호출한다. 결과 보상을 더한 뒤 `EndEpisode`로 닫는다. 결과 보상은 볼넷 `+1`, 삼진 `-1`이다. 판정까지 끝난 인플레이(3단계)는 얻은 베이스마다 `+0.5`, 타점마다 `+0.5`, 그 플레이의 아웃마다 `-0.5`다. 1·2단계 인플레이는 수비가 없어 `0`이다([상황 규칙](game-situation.md)).

중단되면 `AbortPlay()`로 `EpisodeInterrupted` 처리한다. 타석 동안 한 번도 결정하지 않았으면(검증용 시나리오 타구) 에피소드로 닫지 않는다. ML-Agents는 결정 없이 두 번 부른 `EndEpisode`를 무시하고 누적 보상을 이어 가기 때문이다. Agent는 초기화를 직접 요청하지 않는다. 학습기도 모델도 없으면 ML-Agents는 `Heuristic`을 호출한다. 2026-09-26부터 `Heuristic`은 모든 행동을 0으로 두는 **중립 행동**이다. 기준 자세로 서서 공을 보기만 하고 스윙하지 않는다. 존 중앙 공 3개면 삼진으로 에피소드가 끝난다(`3 × -1 - 1 = -4`). 이전의 "0.38초에 15° 스윙" 규칙은 제거했다. 기본 구현은 매 결정마다 경고를 남기므로 재정의는 남겨 둔다. 초기 실험은 [보상 설계](batter-reward-design.md)의 고정 투구·고정 파워 조건을 권장한다.

검증 메뉴는 학습 단계 씬에서 Play Mode를 일시정지한 뒤 `Tools > Baseball Simulation > Verify Batter ML-Agent (Paused Play Mode)`다. 확인 항목은 세 가지다.

- 중립 휴리스틱으로 2개 에피소드가 스윙 없이 끝난다.
- 투구 비행 중 `BallEye`가 `Ball` 태그를 본 결정 수를 보고한다.
- 손잡이를 존 중앙 높이로 낮추고 `OnActionReceived`에 스윙 행동을 직접 넣으면 명령 경계를 거쳐 공에 맞는다. 스윙 시점은 해석기가 계산한 도착 시각(`PlayDirector.PitchArrivalSeconds`)에서 스윙 중앙 위상만큼 앞이다. 이 시점과 자세는 검증 코드에만 있는 고정 입력이다. 실행 기록은 [검증](verification.md)에 둔다.
