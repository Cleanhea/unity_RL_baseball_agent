# 기본 야구 환경 아키텍처

## 1. 목적과 현재 제약

이 문서는 `docs/environment-spec.md`의 계획을 현재 Unity 프로젝트에서 구현하기 위한 최소 구조를 정의한다. 목표는 확장 가능한 거대 프레임워크가 아니라, 투구부터 초기화까지 한 플레이를 읽고 고칠 수 있는 책임 분리다.

2026-09-11 현재 프로젝트에서 확인한 제약은 다음과 같다.

- Unity `6000.5.2f1`, URP `17.5.0`, 3D 물리와 Input System을 사용할 수 있다.
- 빌드에 포함된 `SampleScene`에는 카메라, 조명, Global Volume만 있다.
- 야구 전용 스크립트·프리팹·설정 데이터는 없다.
- `Temp.cs`는 빈 템플릿이라 재사용할 기능이 없다.
- ML-Agents는 설치 선언되어 있지 않으며 현재 설치하지 않는다.
- 에셋은 텍스트 직렬화되지만 씬·프리팹 변경은 Unity Editor를 우선해 참조와 GUID를 보호한다.

## 2. 제안 폴더

구현이 시작되면 기존 템플릿 파일을 임의 정리하지 않고 다음 영역을 추가한다.

```text
Assets/
└─ BaseballSimulation/
   ├─ Scenes/
   │  └─ BaseballPlayground.unity
   ├─ Scripts/
   │  ├─ Core/
   │  │  ├─ PlayDirector.cs
   │  │  └─ SimulationContracts.cs
   │  ├─ World/
   │  │  ├─ FieldLayout.cs
   │  │  └─ BallController.cs
   │  ├─ Actors/
   │  │  ├─ BatterController.cs
   │  │  ├─ RunnerController.cs
   │  │  └─ FielderController.cs
   │  ├─ Input/
   │  │  ├─ ManualPlayController.cs
   │  │  └─ ScriptedPlayController.cs
   │  ├─ Presentation/
   │  │  └─ DebugPresenter.cs
   │  └─ Settings/
   │     └─ BaseballEnvironmentConfig.cs
   ├─ Config/
   │  └─ DefaultBaseballEnvironment.asset
   ├─ Materials/
   └─ Prefabs/              # 재사용 가치가 생긴 객체만 승격
```

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
├─ Field
│  ├─ Ground / Infield / Outfield / FoulVisuals
│  ├─ Home / First / Second / Third
│  ├─ PitchOrigin / PitchTarget
│  └─ PlayBoundary
├─ Actors
│  ├─ Ball
│  ├─ Batter
│  │  └─ Bat
│  ├─ BatterRunner
│  ├─ BallFielder
│  └─ FirstBaseman
├─ Systems
│  ├─ PlayDirector
│  ├─ ManualPlayController
│  ├─ ScriptedPlayController
│  └─ DebugPresenter
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

### 4.2 상태 조회

`GetSnapshot()`에 해당하는 읽기 전용 스냅샷은 플레이 상태, 결과, 시간, 시드, 공, 선수, 베이스, 주자를 한 시점 기준으로 반환한다. 외부 코드는 스냅샷을 통해 내부 컬렉션이나 Transform을 수정할 수 없어야 한다.

HUD는 매 프레임 최신 스냅샷을 표시할 수 있다. 스크립트 검증은 특정 이벤트를 기다린 뒤 스냅샷으로 결과를 확인한다. 향후 학습 연결이 필요해도 먼저 이 상태가 환경 검증에 충분한지 확인하고, 현재 단계에서 벡터 배열과 정규화를 추가하지 않는다.

### 4.3 이벤트 경계

공·타자·주자·수비 컴포넌트는 `BallGrounded`, `BallBatContact`, `BaseReached`, `BallCaught`, `BallPossessionChanged` 같은 사실을 Director에 보고한다. Director는 틱 단위로 모아 우선순위를 적용하고 `OutRecorded`, `RunScored`, `PlayEnded`를 포함한 환경 이벤트를 외부 구독자에게 발행한다.

이벤트 데이터는 변경 불가능한 값으로 만들고 시뮬레이션 시각, 고정 틱, 사건 순번을 포함한다. 이벤트 버스 패키지나 범용 메시징 프레임워크는 필요하지 않다. C# 이벤트 또는 Director의 작은 구독 API면 충분하다.

환경 이벤트는 보상이 아니다. 향후 학습 어댑터가 별도 정책으로 이벤트를 보상에 매핑할 수 있지만 그 코드는 현재 만들지 않는다.

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

## 8. 초기 배치와 재현성

Director는 시작 시 각 재사용 객체의 초기 Transform, 활성 상태, 공 물리 상태를 캡처하거나 명시적 Spawn Transform을 참조한다. 초기화는 객체별 `ResetState`를 정해진 순서로 호출한 후 Director 자신의 큐·타이머·결과를 비운다.

난수가 필요한 시나리오는 환경 전용 난수원과 명시적 시드를 사용한다. Unity 전역 난수 상태를 무심코 공유하지 않는다. 같은 시드, 같은 명령 시간표, 같은 설정은 문제 재현을 돕지만 Unity 물리가 서로 다른 실행 환경에서 비트 단위로 동일하다고 보장하지 않는다.

## 9. 향후 학습 연결 지점

향후 명시적인 학습 단계가 시작되면 학습 컨트롤러는 다음 경계에만 연결한다.

- 명령 경계로 투구, 스윙, 이동, 송구를 요청한다.
- 스냅샷을 읽어 그 단계에서 확정할 관측을 만든다.
- 환경 이벤트를 구독해 별도 보상 정책을 적용한다.
- 에피소드 시작/종료 때 기존 초기화와 결과를 사용한다.

BallController, RunnerController, FielderController를 ML-Agents 타입으로 바꾸지 않는다. 학습 어댑터는 수동·스크립트 컨트롤러와 나란한 입력원이다. 현재는 해당 어댑터, `Agent`, `BehaviorParameters`, 관측 벡터, 행동 공간, 보상 코드를 만들지 않는다.

## 10. 아키텍처 완료 확인

- 결과를 확정하는 곳과 공 소유권을 관리하는 곳이 각각 하나다.
- 수동/스크립트 입력이 동일한 명령 API를 사용하고 직접 상태를 수정하지 않는다.
- 물리 사실과 최종 야구 결과가 분리되어 같은 틱의 우선순위를 적용할 수 있다.
- 디버그 표시는 읽기 전용 스냅샷과 이벤트만 사용한다.
- 모든 런타임 객체가 초기화 계약을 지키고 이전 플레이 큐와 속도를 남기지 않는다.
- 설정값의 소유 위치와 단위가 하나로 정리되어 있다.
- 학습 패키지 없이 전체 환경을 실행할 수 있다.
- 새 추상화가 현재 수동·스크립트 검증에 실제 사용되지 않는다면 추가하지 않는다.
