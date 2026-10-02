# Unity Editor 검증 절차

## 12.29 수비 정지·즉시 송구 수정과 수비만 재학습 (2026-10-02 후속)

사용자 요청: 12.28 구성으로 학습한 수비 분석 결과에 따라 수정 1~3을 적용하고, 처음부터가 아니라 수비만 다시 학습한다. 분석과 규칙은 [수비 정지·즉시 송구 수정](fielding-agents.md#수비-정지즉시-송구-수정-2026-10-02-후속)에 있다.

**변경**

- `FielderAgent.MoveActionGain` 3: 이동 행동에 3을 곱한다(출력 크기 1/3 이상 = 최고 속력).
- `FielderAgent.WriteDiscreteActionMask`: `PlayDirector.CanThrow(index)`가 아니면 송구 1~4를 막는다. `CanThrow`는 타구가 살아 있고 그 수비수가 공을 쥐었는지다.
- 3단계 네 YAML의 수비 `beta` 1e-3→5e-3. `Training/config/stage3_full_team_refield.yaml`: 타자·투수·주자는 `stage3_full_team_9f_try1_20261002` 보존 폴더의 체크포인트, 수비는 새로 시작한다.
- `TrainingStageVerification`: 이동 배율 기대값, 모든 수비 결정의 송구 마스크 검사. `Training/verify_fielder_actions.py`: 네 YAML의 `beta` 0.005, 재시작 YAML의 `init_path` 검사.
- **시도 후 뺀 것:** 포구 후 0.3 s 송구 준비 시간. 검증 A가 1루 포스 아웃에서 세이프로 바뀌었다. 타자주자는 일정한 7 m/s로 약 3.9 s에 1루에 닿고, 준비 시간 0일 때 포스 아웃이 3.90 s다. 준비 시간 측정 검사도 함께 뺐다.

**실행 검증**

- 학습 중 체크포인트 분석(ReferenceEvaluator, 확률 샘플링 노드를 뺀 부분 그래프): 750만·950만 스텝 수비 ONNX의 이동 세기 0.05~0.5, 공에 가장 가까운 수비수의 방향 오차 30~80°, 공을 쥔 8개 역할의 송구 안 함 확률 0~0.01. 체크포인트 `log_sigma` 기준 탐색 표준편차는 최고 속력의 약 6%였다.
- 컴파일: `dotnet build` 오류 0.
- 격리 Unity 6000.5.2f1 배치(씬 변경 없음): 3단계 `TrainingStageVerification` PASS. 9명 이동(배율 3, (0.3, 0.4)는 최고 속력, (0.06, 0.08)은 0.3배), 역할 임무, A~K 결과와 보상 값이 12.28과 같다. 송구 마스크는 공을 쥔 수비수의 128결정에서만 열렸고 나머지 모든 결정에서 막혔다. 스크립트 송구 4회. `MultiArenaVerification` PASS.
- `python -B Training/verify_fielder_actions.py`: 전 항목 PASS(네 YAML 파싱, 수비 `beta` 0.005, 재시작 YAML의 세 `init_path`와 수비 `init_path` 없음).

**확인하지 않은 항목**

- 새 설정으로 실제 학습. 이동 배율과 `beta` 5e-3로 엔트로피 붕괴와 정지 정책을 벗어나는지는 학습해 봐야 한다.
- 송구 마스크는 Unity가 학습기로 보내는 마스크와 ONNX `action_masks` 입력에 들어간다. 실제 학습기 연결 상태의 마스크 전달은 확인하지 않았다.
- 1·2단계·`BenchmarkVerification`은 이번 변경과 관계없어 다시 실행하지 않았다(12.28 PASS).

## 12.28 수비 9명·역할 임무 보상 개편 (2026-10-02)

사용자 요청: 타구가 나오면 수비수가 모두 자기 영역을 벗어나므로, 수비 수를 실제와 같게 늘리고 보상 설계를 개편한다. 규칙은 [수비 9명·역할 임무 보상](fielding-agents.md#수비-9명역할-임무-보상-개편-2026-10-02)에 있다.

**변경**

- `FielderRole`: 투수·포수·1B·2B·3B·SS·LF·CF·RF 9개(값 = 관측 원-핫 위치 = 수비수 인덱스). `TrainingSceneBuilder.FielderLayout`이 9명을 배치한다. 투수 수비수는 투수 몸 자리에 서고 모양 부품을 넘겨받는다.
- `FielderAgent` 관측 65→77(`RoleCount` 9), `RunnerAgent` 관측 57→65.
- `PlayDirector`: 포수는 타구 후 0.3 s 동안 첫 포구를 하지 않는다(`CatcherFoulTipSeconds`).
- `PlayOutcomeRewards`: `FieldingReward` 0.3(개인, 비포텐셜), `TryGetPositionDuty`·`SecondBaseCoverRole`·`CoverPotential`·`PositionReward`(반경 3/6/12 m, 0.005 /m/s, 상한 0.1 /s). `InfieldPotential`·`InfieldPositionReward`·`TryGetCoverBase`를 대체했다.
- `TrainingEnvController`: `BallFielded` 첫 포구에 개인 보상, 베이스별 커버 지표. `TrainingStats`: `Defense Reward/Position`·`Fielding`, `Defense/Cover 1B|2B|3B|Home`.
- `Stage3_FullTeam` 씬을 경기장 4개 × 9명으로 다시 만들었다(씬 GUID 유지). 1·2단계 씬과 `BaseballPlayground`는 바뀌지 않았다.
- `TrainingStageVerification`: 역할 표 독립 계산, `VerifyPositionDuties`, 포구 보상 검사, 9명 스크립트 수비. C·D·J 안타를 분사각 −8°·발사각 12°·34 m/s로 바꿨다(투수가 정면 라이너를 잡고 중견수가 정면에 서 있기 때문). G·J는 홈인과 포수의 홈 태그 아웃을 모두 정상으로 본다.
- `Training/verify_fielder_actions.py`의 합성 관측 크기 65→77.

**실행 검증**

- 컴파일: `dotnet build Assembly-CSharp-Editor.csproj`(출력은 임시 폴더) 오류 0.
- 격리 Unity 6000.5.2f1 복사본 배치 Play Mode(MCP 패키지 제외, 학습기 포트와 다른 `--mlagents-port 5999`, `subst` 짧은 경로):
  - **3단계 `TrainingStageVerification`:** 중립 투구, 9명 이동, 역할 임무 정지 검사, A~K 모두 PASS. 이후 코드 변경은 없다.
    - A 유격수 땅볼: 1루 `ForceOut`, 포구 보상 +0.3이 유격수에게만, 자리 감점 합 −0.183. 타구 순간 커버 Φ_i는 1B −0.099, 2B −0.195(3루 쪽 타구라 2루 커버), 3B −0.078, 포수 0(홈 위).
    - B 좌익수 `FlyOut`(4.30 s). C 좌중간 안타 `RunnerSafe`, 중견수 포구. C 쫓기 비교 +1.045 vs 제자리 +0.171, 1루수 커버 첫 10결정 +0.102 vs +0.010, 자리 감점 합 스크립트 −0.498 vs 제자리 −0.577. 제자리 수비는 `Timeout`, 포구 0.
    - D 2루 세이프, E 3루수 파울 뜬공, F 2루 포스 후 1루 세이프(`Cover 2B`·`Cover 1B` = 1), G 희생플라이 1점, J 2루 주자 홈인, K 리터치 더블 아웃.
  - **같은 코드의 첫 실행:** `MultiArenaVerification`(경기장 4개, 관측 33벡터 최대 차이 0.000008, 중단 0), `BenchmarkVerification`, 2단계·1단계 `TrainingStageVerification` PASS. 이 실행의 3단계 시나리오 실패는 검증 코드 문제였다. 플레이 종료 뒤 초기화된 `BattedBallFielded`를 읽었다. `Defense/Fielded` 지표 비교로 고친 뒤 3단계를 다시 실행해 PASS였다.
- `python -B Training/verify_fielder_actions.py`(miniconda `mlagents`): 전 항목 PASS.

**확인하지 않은 항목**

- 실제 학습. 새 보상으로 포구율·자리 유지·아웃이 개선되는지는 학습해 봐야 한다. 반경 3/6/12 m, 계수 0.005, 포구 0.3은 시작값이다.
- 사용자 원본 Editor에서의 씬 재로드·Console. 배치 실행 중 Unity가 같은 버전의 남아 있던 IL Post Processing 실행기(PID 27380)를 종료했다. 원본 Editor는 다음 컴파일 때 새로 띄운다.
- 송구 30 m/s·포구 즉시 송구라 포수가 생긴 뒤 홈 승부가 수비 쪽으로 기울 수 있다(시나리오에서는 희생플라이·2루 주자 홈인이 성공했다). 송구 준비 시간은 후속 검토 대상이다.
- 독립 실행 파일 재빌드와 기존 3단계 결과 폴더 이동은 하지 않았다.

## 12.27 기존 학습 명령 연결·내야 자리 유지 (2026-10-01 후속)

사용자 요청으로 기존 `mlagents-learn` 명령과 내야 자리 유지 신호를 모두 적용했다. 아래 12.26의 별도 `Training/train.py` 진입점 제한은 이 기록으로 대체한다.

**변경**

- `Training/mlagents_extensions`: ML-Agents 공식 trainer entry point로 수비 전용 POCA를 등록했다. 세 YAML의 `BaseballFielder`만 `baseball_fielder_poca`를 선택한다. 표준 학습기·다른 Agent의 행동 모델을 전역 수정하지 않는다. `Training/train.py`는 일반 CLI 별칭이며 자동 커리큘럼도 표준 CLI로 돌아갔다.
- 현재 `C:\miniconda3\envs\mlagents`에 로컬 패키지를 editable로 등록했다. 다운로드·의존성 변경·설치된 ML-Agents 소스 편집 없이 수행했다. 다른 Python 환경에서는 학습 안내의 설치 명령을 한 번 실행한다.
- `PlayOutcomeRewards.InfieldPositionReward`: 1루수·유격수(2루)·3루수가 공을 처리하지 않을 때, 담당 베이스 반경 3 m 바깥에 초당 `−min(0.05, 0.002 × 초과 거리)` 개인 감점. 첫 결정 뒤 고정 단계 시간으로 누적하고 정상 종료·중단에 환급하지 않는다. 외야수·쫓는 수비수·공 소유자·송구 중인 수비수는 제외한다.
- `TrainingEnvController` 개인 지급·합 보존·초기화, `TrainingStats`의 `Defense Reward/Infield Position` 지표 및 역할 문서를 함께 갱신했다.

**실행 검증**

- `python -B Training/verify_fielder_actions.py`: PASS. 기존 CLI와 같은 plugin discovery/설정 파싱 경로에서 일반·재개·셀프플레이 YAML 모두 수비 확장, beta 0.001, gamma 0.99를 선택했다. 실제 POCA 정책·optimizer 생성과 엄격한 동일 체크포인트 키·가중치 로드가 통과했다.
- 실제 ML-Agents 환경 행동·ONNX 확률/결정론 출력의 방향·크기, 작은/큰/영 벡터, 원본 optimizer 행동 유지, 타자·투수·주자 회귀, ONNX opset 9 checker·ReferenceEvaluator 모두 PASS.
- 자동 커리큘럼 모의 실행: 2·3단계 일반·셀프플레이 모두 기존 `mlagents-learn` 실행 파일을 사용하고 포트·장치 인자를 보존했다. 호환용 `train.py --help`도 기존 옵션을 받았다.
- 격리 Unity 6000.5.2f1 프로젝트에서 전체 컴파일 및 `TrainingStageVerification` 3단계 A~K, `MultiArenaVerification`, `BenchmarkVerification`, 2·1단계 검증 모두 PASS. 검증 포트를 별도로 두고 원본 프로젝트의 씬·에셋은 변경하지 않았다.
- 자리 감점: 0/2.9/3/3.1/4/100 m에서 반경·거리·상한·시간 배율, 외야·쫓기 면제, 비유한/음수 시간과 비활성 플레이 0을 확인했다. 모든 시나리오 고정 단계에서 역할 면제·거리 감점을 독립 계산하고 개인 누적 보상과 정상 종료 합·새 지표를 비교했다.
- 추가 담당 전환·중단 검사: 유격수→3루수 쫓기 변경 후 각자의 차분과 자리 감점만 지급됐다. 중단은 이미 준 자리 감점을 보존했고, 초기화 후 현재 합은 0이며 다음 첫 결정의 개인 누적 보상도 0이었다.
- Unity 로그: 임시 검증 작업공간의 `unity-position-plugin-mapped.log`, `CHASE_BATCH_ALL_PASS`, `POSITION_EXTRA_PASS`. 공백 검사도 PASS.

**확인하지 않은 항목**

- 이 변경으로 새 성능 학습을 시작하거나 사용자의 실행 중인 학습기·Editor를 재시작하지 않았다. 위치 유지 개선·경기장 이탈 감소·반경/계수의 학습 적합성은 미검증이다.
- 관측·행동 수·씬 직렬화는 유지했다. 기존 ONNX에는 과거 성분 클리핑이 남아 있으므로 새 코드로 다시 내보내야 한다. 별도 실행 파일도 새 Unity 코드로 다시 빌드한다.

### 12.26 수비 탐색·개인 쫓기·이동 방향 수정 (2026-10-01)

**변경.** 사용자 요청의 1~3번을 적용했다.

- 일반·재개·셀프플레이 3단계 YAML의 `BaseballFielder.beta`: 5e-3→1e-3. 다른 Behavior 값은 그대로다.
- `PlayOutcomeRewards.ChasePotential`: 첫 포구 전 가장 가까운 수비수 한 명의 개인 포텐셜, 계수 0.02→0.05 /m. 그룹 `DefensePotential`에서 쫓기를 뺐다.
- `TrainingEnvController`: 개인 `AddReward`, 결정별 차분·담당 전환·정상 종료·중단 정산·초기화. `Defense Reward/Chase Shaping` 지표와 수비수별 마지막 플레이 합 조회를 더했다.
- `FielderAgent`: 이동 성분별 클램프를 제거했다. 기존 `FielderController`의 벡터 크기 제한을 사용한다. `Training/train.py`는 ML-Agents 수비 출력도 `/3` 후 벡터 크기 제한으로 바꾸며, 자동 커리큘럼 3단계가 이 진입점을 사용한다. 관측·행동 수, 씬·GUID·직렬화 필드는 바꾸지 않았다.
- `TrainingStageVerification`과 수비·학습·환경·구조 문서를 갱신했다. 4번 스크립트 추적 혼합은 변경 후 학습 결과를 보고 판단할 후속 수단이다.

**실행 검증.**

- Unity 6000.5.2f1 격리 복사본의 배치 Play Mode다. 원본 Editor·학습 결과를 조작하지 않았다. 복사본에서 MCP 패키지를 빼고 짧은 임시 드라이브 경로를 썼다. 런타임·Editor 코드 컴파일 오류는 없었다.
- **3단계 `TrainingStageVerification`:** 중립 투구와 A~K 전부 PASS. 모든 인플레이 결정에서 개인 쫓기 포텐셜은 독립 계산한 가장 가까운 수비수·예상 지점과 일치했다. 그룹에는 포구·포스 커버만 들어갔다. 각 Agent 누적 개인 보상은 자기 쫓기 차분 + 자기 내야 차분과 1e-4 안에서 같았고, 정상 종료의 개인 합은 `−Φ_i(0) + (γ−1)ΣΦ_i(k≥1)`와 같았다. 새 플레이 첫 결정에 이전 개인 보상이 남지 않았다.
- **C 방향 비교:** 첫 포구 전 68결정의 개인 쫓기 보상 총합은 스크립트 추적 +0.788, 제자리 −0.380이었다. 제자리 플레이는 `Timeout`이었다. 이 값은 실제 정책의 학습 성능이 아니라 같은 타구에 대한 스크립트 입력 검증이다.
- **이동:** 수비수 다섯 명 모두 `(4,2)`, `(-2,4)`, `(-4,-2)`, `(2,-4)` 방향 비율과 최고 속력 유지, `(0.3,0.4)` 절반 속력, 0·NaN·Infinity 정지, 위치·속력 초기화가 PASS였다. Agent 행동→Director 명령→수비 몸 적분 경로를 사용했다.
- **담당 전환 추가 검사:** 격리 하네스에서 유격수→3루수로 가장 가까운 수비수를 바꿨다. 이전 담당 포텐셜은 0, 새 담당만 음수였으며 개인 보상 지급이 각자 차분과 같았다.
- **중단·초기화 추가 검사:** `AbortPlay`의 개인 합이 이전 합 + `γΦ_i(지금)−Φ_i(마지막 결정)`과 같았다. 초기화 후 개인 포텐셜·합은 0이었고, 다음 타구 첫 결정의 개인 누적 보상도 0이었다.
- **3단계 `MultiArenaVerification`·`BenchmarkVerification`:** PASS. 병렬 800단계 중단 0회, 경기장 간 수비·주자 관측 최대 차이 0.000008. 기준 투수 2만 표본 존 통과율 50.8%.
- **1·2단계 회귀:** `TrainingStageVerification` PASS. 전체 하네스 5개 작업과 추가 하네스 모두 최종 PASS까지 완료했다.
- **YAML:** 설치된 ML-Agents 1.1.0의 `RunOptions` 스키마로 세 설정을 읽었다. 수비 `beta=0.001`, `gamma=0.99`가 모두 PASS다. `git diff --check`도 PASS다.
- **Python 수비 행동:** 실제 ML-Agents `ActionModel`·Gaussian 분포로 큰·작은·0 벡터의 환경 행동과 확률·결정론 출력 방향을 검증했다. 정책 원본 `(4,2)`의 기본 출력 `(1,0.667)`이 새 진입점에서는 `(0.894,0.447)`이었다. 원본 optimizer 행동과 체크포인트 키, 타자·투수·주자 환경 행동·내보내기·로그 확률은 그대로였고 모든 비교가 PASS다.
- **ONNX:** 실제 수비 행동 모델의 결정론 출력 헤드를 opset 9로 내보내 `onnx.checker`와 `ReferenceEvaluator`로 실행했다. 벡터 크기·방향이 Python 기대값과 1e-6 안에서 같았다. `train.py`와 `auto_curriculum.py`의 구문 검사도 PASS다. 재현 명령은 `python Training/verify_fielder_actions.py`다.
- **실행 진입점:** `Training/train.py --help`가 기존 ML-Agents 옵션을 받았다. 자동 커리큘럼 명령을 모의 실행해 2단계 기존 진입점, 3단계 일반·셀프플레이의 새 진입점, 포트·장치 인자 보존이 모두 PASS였다. 실제 학습은 시작하지 않았다.

**미검증·반영 조건.**

- 표준 `mlagents-learn` 직접 실행과 옛 ONNX에는 내부 성분 제한이 남는다. 3단계는 새 `Training/train.py` 진입점을 사용하고 모델을 다시 내보내야 한다. 설치된 ML-Agents 1.1.0에서만 실행 검증했다.
- 이 수정으로 새 학습을 실행하지 않았다. 표준편차 감소, 포구율·아웃 증가, 계수 0.05 /m의 학습 적합성은 아직 모른다. 담당 전환 포텐셜 변화는 초기 학습에 잡음이 될 수 있다.
- 사용자 원본 Editor의 Console·실시간 Game 뷰는 확인하지 않았다.
- 연결 중인 학습기는 재시작해야 새 `beta`를 읽는다. 독립 실행 파일은 재빌드해야 새 보상·이동 코드가 들어간다. 기존 학습 결과는 보존했고 학습기 재시작·플레이어 재빌드는 수행하지 않았다.

### 12.25 내야 역할 보조 보상 (2026-10-01)

**질문과 확인.** 사용자가 1·2·3루수와 나머지 수비의 보상 설계에 차이가 있는지 물었다. 코드로 확인한 결과 차이는 없었다.

- 결과 보상·시간 감점·12.24 보조 보상은 모두 `SimpleMultiAgentGroup.AddGroupReward`로 다섯 명이 같은 값을 받는다.
- 역할은 관측의 원-핫뿐이다. 커버 항목은 역할과 무관하게 "가장 가까운 수비수"다.
- 이 환경에는 2루수가 없다. 2루는 유격수가 맡는 것으로 설계했다.

**변경.**

- `PlayOutcomeRewards.InfieldPotential`·`DefenseChaser`·`IsBaseInPlay`·`TryGetCoverBase`를 더했다.
- `PlayDirector.LastThrower` 조회를 더했다.
- `TrainingEnvController`가 내야수 개인 보조 보상을 `Agent.AddReward`로 준다. `Defense/Cover 1B`·`2B (SS)`·`3B` 지표를 기록한다.
- 3단계 검증에 결정 시점별 규칙, 수비수별 합, 방향, 지표 검사를 더했다.
- 관측·행동·씬·YAML은 그대로다. 규칙은 [내야 역할 보조 보상](fielding-agents.md#내야-역할-보조-보상-2026-10-01)에 있다.
- **설계 중 바꾼 것:** 처음 안은 계수를 주자 유무로 0.02/0.01로 바꾸고 송구한 수비수를 빼지 않았다. 검토에서 두 문제가 나와 계수를 하나로 하고 송구 중인 수비수를 뺐다. 주자가 세이프로 도착하는 순간 멀리 있던 내야수가 양의 보상을 받는 문제와, 송구 순간 자기 베이스와 먼 만큼 감점되는 문제다.
- **ML-Agents 확인:** 설치된 `mlagents` 1.1.0(`miniconda3/envs/mlagents`)의 `ExtrinsicRewardProvider.evaluate`는 개인 보상(`ENVIRONMENT_REWARDS`)에 그룹 보상(`GROUP_REWARD`)을 더한다. 그래서 MA-POCA에서 개인 `AddReward`가 그 수비수에게만 들어간다.

**실행 검증.**

- **컴파일:** Unity가 만든 `Assembly-CSharp(-Editor).csproj`를 `dotnet build`(출력은 임시 폴더)로 빌드했다. 오류가 없었다. 경고는 기존 obsolete API 경고뿐이다.
- **실행 환경:** 원본 Editor와 분리한 격리 복사본(Unity 6000.5.2f1, `-batchmode -nographics`)이다. 경로 길이를 줄이려고 `subst` 드라이브를 썼다. 복사본 전용 하네스가 씬마다 Play Mode에 들어가 일시정지하고 검증을 하나씩 실행했다. 복사본에서는 MCP 패키지를 뺐다. 원본 Editor 세션과 MCP 포트가 겹치지 않게 하기 위해서다. 프로젝트 코드는 이 패키지에 의존하지 않는다.
- **3단계 `TrainingStageVerification`:** 중립 투구와 A~K가 PASS다. 결과·그룹 보조 보상 값은 12.24와 같았다(A 타구 순간 Φ −0.539, 합 +0.528 / C 쫓기 +0.420 vs 제자리 −0.014). 내야 역할 보조 보상은 다음과 같다.
  - **모든 인플레이 시나리오:** 결정 시점마다 Φ_i가 따로 계산한 규칙과 1e-5 안에서 같았다. 외야수·공을 쥔 수비수·송구 중인 수비수는 0, 그 밖 내야수는 −0.02 × 베이스 거리, 첫 포구 전 쫓는 수비수 한 명만 0이었다. 수비수마다 개인 보조 보상 합이 `−Φ_i(0) + (γ−1)ΣΦ_i(k≥1)`과 1e-4 안에서 같았다.
  - **A 유격수 땅볼:** 타구 순간 1B −0.099, SS −0.198, 3B 0(이 순간 쫓는 수비수)이었다. 38결정 합은 1B +0.111, SS +0.237, 3B +0.024였다. `Defense/Cover 1B` = 1이었고 2·3루 지표는 기록되지 않았다.
  - **C 다시 치기:** 첫 10결정의 1루수 합이 1루로 뛴 쪽 +0.102, 제자리 +0.010이었다. `Defense/Cover 1B`는 1과 0이었다.
  - **F 1루 주자 유격수 땅볼:** 합은 1B +0.112, SS +0.213, 3B +0.033이었다. `Defense/Cover 2B (SS)` = 1, `Defense/Cover 1B` = 1이었다.
- **3단계 `MultiArenaVerification`:** PASS. 800단계 동시 진행에서 중단 0회였다. 경기장 사이 수비·주자 관측 최대 차이는 0.000008이다.
- **3단계 `BenchmarkVerification`:** PASS. 기준 투수 2만 표본의 존 통과 비율은 50.8%였다.
- **회귀:** 2단계·1단계 `TrainingStageVerification`이 PASS였다. 5개 작업 모두 PASS, Unity 종료 코드 0이다.
- **로그의 예외:** 복사본에서 뺀 `Library/Search` 색인과 Editor 내부 ILPP 실행기에서 나온 것뿐이다. 프로젝트 코드 예외·컴파일 오류는 없었다.

**미검증.**

- 실제 학습에서 내야수가 베이스를 덮고 아웃이 늘어나는지. `mlagents-learn`은 실행하지 않았다.
- 계수 0.02/m가 적절한지. 쫓는 수비수가 바뀔 때 Φ_i가 뛰는 크기(거리 10 m에 0.2)가 학습을 방해하는지.
- MA-POCA 공유 가치함수에 수비수마다 다른 개인 보상이 섞일 때의 편향 크기.
- 사용자 원본 Editor의 컴파일. 원본 Editor는 건드리지 않았다.

### 12.24 3단계 수비 보조 보상 (2026-10-01)

**원인 확인.** 사용자가 "수비수가 경기장 밖으로 나가려 한다"고 보고했다. `Training/results/stage3_full_team`(수비 4,937,600스텝)을 확인했다.

- **TensorBoard(수비 Behavior, 5구간 평균):** `Play End/Timeout` 0.989~0.999, `Play/Outs` 0.0007~0.010, `Play End/Runner Safe` 0, `Play/Live Time` 11.4~11.5 s.
- **체크포인트 재현:** `BaseballFielder/*.pt`의 정책을 NumPy로 그대로 계산해 단순화한 땅볼·타자주자로 11.5 s를 돌렸다.
  - 타구 방향(−30°·0°·+30°)과 관계없이 도착 위치가 같았다.
  - 3.0M에서는 3루 쪽 파울·홈 뒤, 4.94M에서는 1루 쪽 파울 지역이었다. 1B (16, 23)→(57, 1), SS→(59, 27). 방향이 체크포인트마다 바뀌었다.
  - 행동 평균 |μ|는 1.0~1.8, σ는 약 1이었다.
  - 공 궤적·주자를 단순화한 재현이며 Unity 화면 확인은 아니다.

**변경.** `PlayOutcomeRewards.DefensePotential`과 `TrainingEnvController`의 수비 결정 시점 보조 보상 `γΦ' − Φ`, 끝 `−Φ`를 넣었다. `Defense/Fielded`·`Defense Reward/Outcome`·`Shaping` 지표를 더했다. 3단계 YAML 세 개의 수비 `gamma`에 주석을 달았다. 관측·행동·씬은 그대로다. 규칙은 [수비 보조 보상](fielding-agents.md#수비-보조-보상-2026-10-01)에 있다.

**실행 검증.** 원본 Editor와 분리한 격리 복사본(Unity 6000.5.2f1, 긴 경로)의 배치 하네스다. 기존 씬을 그대로 썼다. 종료 코드 0, 5개 작업 모두 PASS다.

- **3단계 `TrainingStageVerification`:** 중립 투구와 A~K가 기존 기대 결과대로 PASS다. 인플레이 시나리오마다 컨트롤러의 보조 보상 합이 따로 모은 결정 시점 Φ로 계산한 `−Φ(0) + (γ−1)ΣΦ(k≥1)`과 1e-4 안에서 같았다.
  - **A 유격수 땅볼:** 타구 순간 Φ −0.539. 첫 포구 결정에서 −0.055 → +0.238로 올랐다. 38결정, 합 +0.528이다. `Defense/*` 지표가 결과·보조 보상 값과 같았다.
  - **C 가운데 안타 다시 치기:** 첫 포구 전 68결정의 보조 보상 합이 스크립트 수비(쫓기·1루 커버) +0.420, 제자리 수비 −0.014였다. 제자리 수비는 공을 잡지 못해 `Timeout`으로 끝났다. 할인 없는 합은 +1.010이었다.
- **3단계 `MultiArenaVerification`·`BenchmarkVerification`:** PASS. 800단계 동시 진행에서 중단 0회였다. 경기장 사이 수비·주자 관측 최대 차이는 0.000008이다.
- **회귀:** 2단계·1단계 `TrainingStageVerification`이 PASS였다.
- **로그의 예외:** AppUI 네이티브 플러그인과 복사본 PackageCache 경로 길이에서 나온 것뿐이다. 프로젝트 코드 예외·컴파일 오류는 없었다.

**미검증.**

- 보조 보상으로 실제 학습했을 때 수비가 공을 쫓고 아웃을 만드는지. `mlagents-learn` 실행은 하지 않았다.
- 계수(0.02/m, 0.25)가 적절한지.
- 사용자 원본 Editor의 컴파일. 원본 Editor는 건드리지 않았다.

### 12.23 3단계 수비 이동·초기 학습 상태 확인 (2026-09-30)

- **저장된 학습 상태:** `Training/results/stage3_full_team`의 타자 9,272 / 투수 250 / 수비 320 / 주자 64스텝. 수비·주자 `checkpoint.pt`의 `Optimizer:adam.state`는 비어 있어 업데이트가 없었다. 기존 수비 설정은 `buffer_size=20480`, 설치된 MA-POCA는 수집 경험이 이 값을 넘어야 업데이트한다. 기록된 짧은 실행으로 수비 능력이 생겼다고 볼 수 없다.
- **실행 검증:** 원본 Editor와 분리된 임시 프로젝트, Unity 6000.5.2f1 배치 Play Mode. 기존 `TrainingStageVerification.Run()`의 중립 투구 및 A~K(포구·송구·포스·태그·주루·득점·리터치)와 지표 검사가 모두 PASS였다.
- **이동 경로 검증:** 임시 하네스에서 시나리오 타구를 시작한 뒤 다섯 `FielderAgent.OnActionReceived()`에 `(±0.6, 0.8)` 이동, 송구 0을 한 번 넣었다. 이후 다른 입력 없이 Director/실제 Physics를 25회 진행했다. 다섯 수비수 모두 방향이 맞고 0.5 s에 약 1.80 m 이동, 속력 약 7.20 m/s였다. 이어 초기화한 위치는 시작 위치와 같고 속력은 0이었다. 이동 명령 유지·가속·초기화가 동작한다. **학습된 정책의 수비 성능 검사는 아니다.**
- **설정 변경:** 3단계 일반·셀프플레이 수비 `batch_size=256`, `buffer_size=4096`, `summary_freq=2000`. 기존 체크포인트 재개용 `stage3_full_team_resume.yaml`에는 네 Behavior 모두 `init_path`가 없다. 관측·행동·보상·씬은 변경하지 않았다.
- **설정 검증:** 세 YAML 모두 설치된 ML-Agents 1.1.0의 `RunOptions` 스키마 검사를 통과했다. 재개용 설정은 신규 실행용 설정에서 `init_path`만 제거한 것과 같고, 기존 네 Behavior의 `checkpoint.pt`가 모두 존재한다. `git diff --check`도 통과했다.
- **미검증:** 수정한 설정의 장시간 학습, 타구 추적·포구·송구 능력의 향상, 사용자 원본 Game 뷰에서의 실시간 움직임. 학습기 재실행 없이 설정이 반영된다고 보지 않는다. 로그는 `Training/results/fielder-check-import.log`, `fielder-check-run.log`(Git 제외)에 있다.

### 12.22 투수 5×5 격자 씬 반영·윗줄 바깥 칸 보정 (2026-09-29)

2단계 학습(2026-09-27 19:39~22:48)에서 투수가 볼만 던진 원인을 확인하고, 이미 코드에 들어간 이산 격자 행동을 씬에 반영했다. 원본 Editor는 건드리지 않았다. 격리 복사본(Unity 6000.5.2f1, 긴 경로) 배치 하네스로 확인했다.

- **원인 확인:** 그 학습의 투수 체크포인트는 연속 3 + 이산 `[5]`(위치 연속 좌표) 구조였다. 위치 평균이 행동 경계 ±1 밖 ±10~22로 포화돼 있었다. TensorBoard에서 투수 약 88k→94k 스텝 사이에 볼 0.46→0.99, 볼넷 0.10→1.00이 됐고 끝(124k)까지 그대로였다. 투수 누적 보상은 +0.9에서 -1.0으로 나빠졌다. 이산 격자 코드(22:53 수정)는 학습이 끝난 뒤 들어갔고, 씬은 다시 만들지 않은 상태였다([투수 Agent](pitcher-agent.md#행동-1--3)).
- **씬 재생성:** `TrainingSceneBuilder.Build`로 2·3단계 씬을 경기장 4개로 다시 만들었다. 백업과 비교했다. fileID를 무시하면 바뀐 것은 투수 Behavior(연속 3 → 1, 분기 `[5]` → `[5, 5, 5]`, 경기장 4개 모두)와 새로 직렬화된 `scriptedLocationSpread` (0.25, 0.28)뿐이다. 플레이그라운드·1단계 씬은 바이트 단위로 같고, 씬 `.meta` GUID도 그대로다.
- **첫 검증 실패:** `TrainingStageVerification` 2단계가 `Curve at outer cell (2, 4) is a ball (got CalledStrike)`로 실패했다. 5구종 × 25칸 × 구속 최소·중간·최대 375구 진단에서 10구가 윗줄 바깥 칸(높이 칸 4) 스트라이크였다. 느린 커브·슬라이더·체인지업이 플레이트 뒤쪽에서 입체 존 윗면을 스쳤다.
- **윗줄 실측:** 윗줄 가운데 3열에서 볼이 되는 최소 목표 높이를 이분 탐색했다(구종 5 × 구속 3 × 열 3). 가장 높은 값은 커브 110 km/h 가운데 열 1.152 m(앞 모서리에서 공 아랫면 8.5 cm 존 위)였다. 포심은 1.094~1.113 m였다.
- **수정:**
  - `PitcherAgent.TopRowLift` = 0.05 m. 윗줄만 1.122 → 1.172 m로 올렸다.
  - `TrainingStageVerification` 2단계의 바깥 칸 판정을 완화했다. 공이 실제로 입체 존에 닿았으면(`InZone`) 스트라이크를 허용한다. 대신 판정·투구 보상이 존 검사와 일치해야 하고, 그런 투구가 바깥 칸 투구의 5%(`MaxOuterCellStrikeRate`)를 넘으면 실패다. 안쪽 3×3은 여전히 모두 스트라이크여야 한다.
- **재검증(종료 코드 0):**
  - 2·3단계 × `TrainingStageVerification`·`MultiArenaVerification`·`BenchmarkVerification` 6종 모두 PASS다.
  - 2단계 Director 투구 125구: 안쪽 45구 스트라이크, 바깥 80구 볼, 존을 스친 바깥 칸 스트라이크 0, 최대 통과 오차 1.28 cm였다. 격자 높이는 0.388~1.172 m다.
  - 375구 진단(구속 최소·중간·최대) 불일치 0이다.
- **미확인:** 새 격자로 한 2단계 학습, 플레이어 재빌드. 투수는 행동 구조가 바뀌어 옛 체크포인트를 이어 쓸 수 없다.

### 12.21 고정 상대 평가·승패 지표·셀프플레이 설정 검증 (2026-09-27)

[고정 상대 평가](training-curriculum.md#고정-상대-평가-2026-09-27)와 [셀프플레이 설정](training-curriculum.md#동시-학습과-셀프플레이)을 확인했다. 원본 Editor는 건드리지 않았다. 격리 복사본(Unity 6000.5.2f1, 긴 경로) 배치 하네스로 확인했고, 학습기는 실제 mlagents 1.1.0으로 연결했다.

- **배치 하네스(종료 코드 0):**
  - 경기장 4개로 1~3단계 씬을 다시 만든 뒤 플레이그라운드 회귀 6종을 돌렸다.
  - 단계마다 `MultiArenaVerification`·`BatterAgentVerification`·`TrainingStageVerification`을 돌렸다. 2·3단계는 새 `BenchmarkVerification`도 돌렸다. 모두 PASS다.
  - 기존 검증은 시작할 때 평가 타석을 끄고, 진행 중인 평가 타석을 끝낸 뒤 진행한다(`BatterAgentVerification.DisableBenchmarks`).
  - 로그의 예외는 복사본 PackageCache 경로 길이, App UI 네이티브 플러그인, 학습기 없는 gRPC 초기화에서 나온 것뿐이다.
- **씬 반영:** 반영 전 백업과 비교했다. fileID를 무시한 블록 단위로, 단계마다 바뀐 블록은 `TrainingEnvController` 4개뿐이다. 새 필드 4개가 들어갔고, 2·3단계는 모델 GUID `32d22bce…`를 참조한다. 1단계는 모델이 비어 있다. 블록 수는 같다(5313/5377/5905). 플레이그라운드 씬은 바이트 단위로 같다.
- **기준 투수 분포:** 표본 2만 개의 구종 비율은 포심 34.3%, 투심 17.0%, 슬라이더 23.5%, 체인지업 13.3%, 커브 11.9%로 목표와 ±1.5%p 안이다. 존 통과는 50.8%다. 모두 구종 구속 범위와 투수 Agent 위치 범위 안이다.
- **기준 투수 타석(2·3단계):** 중립 타자로 삼진까지 6구를 던졌다. 구종이 섞였다.
  - 그동안 투수 Agent는 타석·투구가 열리지 않았고 보상도 0이었다.
  - 타자는 한 에피소드로 닫혔다.
  - `Benchmark Batter` 지표는 투구마다·타석마다 한 번씩 기록됐다. 일반 지표(`Pitch Type`·`Plate Appearance`·`Matchup`)는 늘지 않았다.
- **고정 타자 타석(2·3단계):**
  - 타석 동안 타자 Behavior가 `InferenceOnly`와 벤치마크 모델로 바뀌었다.
  - 중립 휴리스틱은 스윙하지 않는데 모델이 스윙했다. 2단계는 홈런(결과 +2.50), 3단계는 단타(+0.50)였다.
  - 타석이 끝난 뒤 Behavior 종류·모델이 원래대로 돌아왔다.
  - 투수는 이 타석을 한 에피소드로 닫았다. 지표는 `Benchmark Pitcher`에만 들어갔다.
- **승패 지표:** 2단계 볼넷 → `Matchup/Batter Win` 1, 삼진 → `Pitcher Win` 1, 인플레이(결과 0) → `Draw` 1. 3단계 포스 아웃 → `Pitcher Win` 1, 단타 → `Batter Win` 1.
- **설정 파싱:** `stage2_batter_pitcher_selfplay.yaml`·`stage3_full_team_selfplay.yaml`을 mlagents `RunOptions`로 읽었다. 타자·투수에만 `self_play`가 있고 값은 문서 표와 같다. `auto_curriculum.py --self-play`는 2·3단계 설정·run-id만 바꾼다.
- **실행 파일 빌드:** `auto_curriculum.py --build-only`로 1~3단계를 다시 빌드했다(컴파일 오류 0). `Training/builds/`의 이전 실행 파일은 이 빌드로 바뀌었다.
- **2단계 셀프플레이 실행 시험:** 스크래치 폴더 결과, 교대 값을 줄인 설정을 썼다(타자 `team_change` 4,000 / 투수 175). 1단계 체크포인트에서 시작했다.
  - 타자 24,000 / 투수 1,050스텝이 141초에 함께 끝나 두 모델을 내보냈다.
  - TensorBoard 기록 시각이 타자 → 투수 → 타자 순으로 번갈아 나타났다. 한 번에 한 팀만 배운다.
  - `Self-play/ELO`, `Matchup/*`, `Benchmark Batter/*`·`Benchmark Pitcher/*`가 두 Behavior 로그 모두에 기록됐다. `Env/Aborted Play`는 0이다.
  - `Player-0.log`에 예외는 없다. 실행 파일 안의 추론 전환도 오류 없이 동작했다.
  - 같은 실행에서 잰 결정 비율은 타자 49.4결정/2.13구 = 투구당 23.3, 투수 투구당 1.0이다. 설정값 환산 근거(23)와 맞는다.
- **3단계 셀프플레이 실행 시험:** 위 2단계 시험 체크포인트에서 타자·투수를 시작했다. 셀프플레이는 타자·투수에만 넣었다.
  - 네 Behavior가 모두 연결됐다(`BaseballBatter?team=0`, `BaseballPitcher?team=1`, `BaseballRunner?team=0`, `BaseballFielder?team=1`). 약 150초에 모두 모델을 내보냈다.
  - 타자·투수 기록 시각은 네 번 번갈아 나타났다. 주자·수비는 교대와 무관하게 처음부터 끝까지 계속 기록됐다. 셀프플레이 학습기와 일반 MA-POCA 학습기가 한 실행에서 함께 동작한다.
  - `Benchmark Batter/On Base`·`Benchmark Pitcher/On Base`가 기록됐다. `Env/Aborted Play`는 0, `Player-0.log` 예외는 0이다.
  - 시험 설정은 주자·수비 `max_steps`가 작아 먼저 도달했다. 그 뒤로도 타자·투수가 끝날 때까지 스텝이 계속 늘고 체크포인트를 저장했다(업데이트는 없음). 실제 설정에서도 가장 늦게 끝나는 Behavior까지 실행이 이어진다.
- **미확인:** 실제 크기 설정의 장시간 셀프플레이 학습과 순환 억제 효과, 원본 대화형 Editor의 Play, 평가 지표의 장기 추이.

### 12.20 2·3단계 실행 파일 스크립트 누락 수정과 2단계 자동 학습 시작 (2026-09-27)

1단계가 3,000,042스텝으로 끝났다. 이어서 16:15에 `auto_curriculum.py --start-stage 2`로 시작한 2단계 학습기가 `UnityTimeOutException`으로 종료됐다. 실행 파일 로그(`Player-0.log`)에 `The referenced script ... is missing!`이 82회 있었다. 16:24와 16:29에 다시 만든 실행 파일도 확인했다. 빌드 로그에 `Script attached ... is missing` 150회, 실행 시험에도 같은 누락이 있었다. 모든 MonoBehaviour가 빠져 Agent가 없으니 학습기가 연결을 기다리다 시간이 초과된 것이다.

- **원인:** 이 PC의 TEMP가 8.3 짧은 경로(`C:\Users\MRHONG~1\...`)다. `tempfile.gettempdir()` 아래에 만든 빌드용 복사본을 그 경로로 열어서 프로젝트 MonoScript가 연결되지 않았다(12.10 이전 격리 복사본에서 본 현상과 같다).
- **수정:** `WORK`를 `Path(tempfile.gettempdir()).resolve()`로 바꿔 긴 이름 경로(`C:\Users\mr hong\AppData\Local\Temp\baseball-curriculum-build`)를 쓴다. 길이는 짧은 경로와 같아서 Burst 경로 길이 문제는 없다. 스크래치 복사본처럼 긴 경로에서는 Burst 빌드가 실패했다. `CurriculumPlayerBuild`는 누락 스크립트가 있으면 빌드 전에 중단한다.
- **재빌드:** `--start-stage 2 --build-only`로 2·3단계를 다시 빌드했다. 두 빌드 모두 성공했고 빌드 로그의 누락 스크립트는 0회다.
- **실행 시험(결과는 임시 폴더, 원본 `Training/results` 아님):**
  - 2단계: 1단계 체크포인트에서 시작해 타자 3,000 / 투수 150스텝 학습 후 두 모델을 내보냈다. `Player-0.log` 누락 0, 조준 실패 0, 중단 0. TensorBoard `Batted Ball/Distance (m)` 79~96 m(수정 전 612.9 m), `Env/Aborted Play` 0.
  - 3단계: 위 2단계 시험 체크포인트에서 시작했다. 타자·투수·주자·수비 네 Behavior가 모두 등록돼 모델을 내보냈다. 누락 0, 조준 실패 0, 중단 0.
- **학습 시작:** 실패한 2단계 시작 폴더를 `Training/results/stage2_batter_pitcher_failed_20260927_1616`으로 옮겨 보존했다. 18:40에 새 콘솔 창에서 `auto_curriculum.py --start-stage 2 --skip-build`를 시작했다. 시작 후 약 2.5분 동안 타자 60,000스텝, 투수 2,000스텝 진행을 확인했다. 초기 속도로는 2단계에 약 6~7시간(투수 400,000스텝이 가장 늦음) 걸린다.
- **미확인:** 2단계 완료와 3단계 자동 전환, 장시간 학습 성능. 16:23~16:24에 만든 실패 빌드는 다른 세션이 `Training/builds/*_failed_20260927_1625`로 옮겨 두었다. 16:29 빌드는 이번 재빌드로 덮어썼다.

### 12.19 병렬 경기장 투구 조준·비거리 수정 검증 (2026-09-27)

1단계 학습 로그에서 3번 경기장의 일부 포심 투구가 `투구 조준이 수렴하지 않았다 (남은 오차 0.001 m)`로 거부됐다. 학습은 계속 진행돼 `BaseballBatter` 3,000,042스텝의 최종 모델이 저장됐지만, 마지막 TensorBoard 구간의 `Env/Aborted Play`는 평균 약 0.004였다. 같은 구간의 `Batted Ball/Distance (m)` 평균 612.9 m는 병렬 경기장의 월드 X 오프셋이 포함된 잘못된 값이다. 이 과거 이벤트 값은 수정 후에도 바뀌지 않으며, 모델 보상에는 사용되지 않았다.

- **원인과 수정:** 투구 해석기가 월드 좌표에서 매 단계를 적분해 먼 경기장에서 float 반올림 오차가 누적됐다. 시작점 기준 변위로 적분하도록 바꿨다. 타구 스냅샷의 `FirstTouchPoint`는 기존대로 월드 좌표를 유지하고 `Distance`만 해당 경기장 홈 기준으로 계산한다.
- **투구 전수 검사:** 격리 복사본(Unity 6000.5.2f1)의 네 경기장에서 5구종을 110~160 km/h, 0.1 km/h 간격으로 총 10,020회 조준했다. 수정 전 같은 방식의 검사에서는 2번 경기장 2회, 3번 경기장 18회 실패했고, 수정 후에는 모두 성공했다.
- **회귀:** 격리 복사본에서 플레이그라운드 물리·판정·주루·보상·상황 검사와 1~3단계 `MultiArenaVerification`·`BatterAgentVerification`·`TrainingStageVerification`을 다시 실행했다. 세 단계 모두 800 고정 단계 동시 진행에서 중단 0회, 실제 홈플레이트 통과점의 최대 목표 오차는 1단계 0.87 cm, 2·3단계 0.69 cm였고 전체 하네스 종료 코드는 0이다. 각 경기장의 홈 기준 비거리 50 m와 첫 닿음 월드 좌표 보존도 확인했다.
- **미확인:** 원본 대화형 Editor의 수정 후 장시간 Play·재학습과 새 실행 파일의 장시간 학습 성능. 기존 1단계 ONNX 모델은 완료됐으며, 이 수정만을 위해 재학습하지 않았다.

### 12.18 병렬 경기장 복제 검증 (2026-09-27)

**실행 방식.** 12.15와 같은 격리 복사본 배치 하네스(Unity 6000.5.2f1, 긴 경로)를 썼다. 한 번 실행에 다음을 모두 한다: 경기장 4개로 1·2·3단계 씬 생성 → 플레이그라운드 회귀 → 단계마다 `MultiArenaVerification`·`BatterAgentVerification`·`TrainingStageVerification`. 최종 코드 기준 실행의 종료 코드는 0이었다. 로그의 예외는 복사본 PackageCache 경로 길이와 App UI 네이티브 플러그인에서 나온 것뿐이고, 프로젝트 코드 예외나 컴파일 오류는 없었다.

- **씬 구성:** 단계마다 `TrainingEnvController` 4개, `arenaIndex` 0~3이다. Agent 수는 1단계 4, 2단계 8, 3단계 44다. `DebugPresenter`는 0번 경기장만 켜져 있다. 반영 전 백업과 비교하면 경기장 루트 아래 Transform과 컴포넌트가 정확히 4배이고 루트 이름만 `(Arena 1~3)`이 붙었다. 카메라·조명·Global Volume·씬 설정은 하나 그대로다. 플레이그라운드 씬은 바이트 단위로 변하지 않았다. 씬 `.meta` GUID도 그대로다.
- **참조 격리:** 세 단계 모두 각 컨트롤러·Director·Agent·수비·주자 참조가 자기 경기장 루트 안에 있다. 가장 가까운 경기장 간격은 400 m다.
- **동시 진행:** 800 스텝 동안 모든 경기장이 스스로 진행했다. 공은 자기 홈에서 최대 18.4 m 안에 있었고, 중단된 플레이는 0개다.

| 단계 | 경기장별 투구 수 | 첫 투구 구속(km/h) |
| --- | --- | --- |
| 1 | 23/23/24/23 | 139.5 / 123.6 / 133.9 / 125.4 |
| 2 | 25/25/25/25 | 145.0 × 4 (학습기 없는 투수 중립 행동) |
| 3 | 25/24/24/24 | 145.0 × 4 (같은 이유) |

- **경기장별 시드:** 첫 실행에서는 시드를 `arenaIndex × 7919`만큼 더했다. 그러자 1단계 첫 구속이 139.5/142.1/144.7/147.3으로 일정 간격이 됐다. `System.Random`이 시드 간격에 선형으로 반응한 것이다. 0번은 원래 시드를 그대로 쓰고 나머지는 해시로 섞도록 고쳤다. 다시 실행한 결과는 위 표처럼 서로 독립된 값이다.
- **홈 기준 관측(3단계):** 무작위 상황을 끄고 초기화했을 때 경기장마다 홈 기준 수비 위치가 0.00 mm 차이로 같았다. 같은 상황(1루 주자)과 같은 타구를 모든 경기장에 넣고 11스텝 진행했다. 수비·주자 관측 벡터 21개가 경기장 사이에서 최대 0.000008 차이로 일치했다.
- **회귀:** 플레이그라운드의 투구 판정·타구·주루·타자 평가·타자 보상·경기 상황 검증과 1~3단계 규칙 검증이 모두 PASS다. 3단계 시나리오 A~K 결과는 12.15와 같다.
- **미확인:** 원본 대화형 Editor에서 4개 경기장 씬을 연 Play·학습, 경기장 수에 따른 실제 프레임 속도, 새 씬으로 다시 빌드한 `Training/builds/` 실행 파일.

### 12.17 TensorBoard 야구 지표 검증 (2026-09-27)

원본 Editor는 건드리지 않았다. 격리 복사본 배치 모드(Unity 6000.5.2f1)에서 컴파일과 검증을 했고, 학습기는 실제 mlagents 1.1.0으로 연결했다.

- **컴파일·단계 검증:** 컴파일 오류 없이 1·2·3단계 씬 모두 `TrainingStageVerification.Run()` PASS. 새로 넣은 지표 검사는 다음과 같다.
  - 1단계: 중앙 스트라이크 12개 뒤 `Called Strike`·`Zone Rate`=1, `Swing Rate`=0, 삼진 타석 `Pitches`=3, 구속 범위 안, 구종·출루 지표 없음
  - 2단계: 볼넷(`Pitches`=4, `Chase Rate`=0, `Four Seam`=1), 삼진, 컨택 투구(`Contact Rate`·`Zone Swing Rate`·`Fair`=1). 기록값은 타구 100.2 km/h, 발사각 14.7°, 타이밍 +20 ms였다.
  - 3단계: 시나리오별 값 일치
    - 포스 아웃: `Play/Outs`=1, `On Base`=0
    - 단타: `Batter Bases`=1, `Runner Safe`=1, `On Base`=1
    - 희생플라이: `Runs`=1
    - 만루 볼넷: `On Base`=1
    - 2아웃 삼진: `Half Inning/Runs`=0
- **실제 TensorBoard 기록:** 같은 코드로 1~3단계 실행 파일을 짧은 경로의 복사본에서 빌드했다. 긴 스크래치 경로에서는 PackageCache 경로 길이 때문에 Burst 단계가 실패해서 경로를 옮겼다. 결과는 원본 `Training/`이 아니라 임시 폴더에 남겼다.
  - 1단계 6,000스텝: `BaseballBatter` 로그에 야구 지표 25종과 `Swing/Timing Error (ms)_hist` 히스토그램이 기록됐다. 학습 초기 정책은 첫 결정에서 바로 스윙해 모두 헛스윙이었다(타이밍 약 -335 ms). 그래서 타구 지표는 나오지 않았다.
  - 2단계(타자 3,000 / 투수 150스텝): 타자·투수 로그 모두에 같은 야구 지표 31종이 기록됐다. 구종 비율은 5종 각각 약 0.12~0.23이었다. 점 간격만 두 Behavior의 `summary_freq`에 따라 달랐다.
- **미확인:** 원본 대화형 Editor에서의 Play 학습, 실제 3단계 학습 중 `Play`·`Half Inning` 지표의 TensorBoard 기록(수비 결과는 Editor 시나리오로만 확인), 장시간 학습에서 지표 추이.

### 12.16 단계 자동 전환 연결 검증 (2026-09-27)

- 원본 Editor에서 진행 중인 1단계 학습(PID 18100)은 유지했다. 별도 복사본의 배치 Unity로 2·3단계 Windows 실행 파일을 빌드했다. 두 빌드 모두 성공했으며 원본 `Assets`·씬을 빌드용으로 수정하지 않았다. 빌드 로그에 긴 PackageCache 경로의 import 예외가 있었으나 실행 파일 연결 시험은 통과했다. 다음 빌드의 임시 복사본 경로는 짧은 시스템 임시 경로로 바꿨다.
- 현재 1단계 체크포인트에서 시작한 짧은 2단계 학습으로 Unity 통신 1.5.0 연결, 타자·투수 Behavior 등록, 두 최종 모델 생성을 확인했다.
- 위 2단계 시험 체크포인트에서 시작한 짧은 3단계 학습으로 타자·투수·주자·수비 네 Behavior 등록과 네 최종 모델 생성을 확인했다.
- 실제 실행 환경의 PyTorch 2.7.1+cu128에서 기존 1단계 체크포인트를 `--torch-device cpu`로 읽으면 CPU·CUDA 텐서 불일치로 중단됐다. 자동 실행은 ML-Agents 기본 장치 선택을 사용하도록 고쳤고, 같은 체크포인트의 2·3단계 짧은 학습이 종료 코드 0으로 끝났다.
- 자동 전환 검사에 짧은 실행 결과를 넣었을 때 두 단계의 모든 Behavior를 완료로 확인했고, 아직 1단계 최종 모델이 없는 현재 상태에서는 2단계 전환을 거부했다.
- `auto_curriculum.py --attach-stage1-pid 18100 --skip-build` 감시 프로세스를 시작해 `waiting_stage1` 상태를 확인했다. 실제 300만 스텝 종료 후 2단계 시작, 2단계 전체 학습 종료 후 3단계 시작, 장기 학습 성능은 아직 확인 전이다. 상태 파일과 단계별 로그는 `Training/results/`에 남는다.

### 12.15 후속 작업 검증 — 구종 보정·볼카운트·주자 여러 명·결과 보상 (2026-09-26)

**실행 방식.** 12.12와 같은 격리 복사본 배치 하네스를 쓰되, 이번에는 한 번 실행으로 1·2·3단계 씬을 차례로 모두 검증하도록 넓혔다. 최종 코드 실행은 종료 코드 0이었다. 반영 전 씬 확인 결과는 다음과 같다.

- 오브젝트 이름 목록을 순서 무시로 비교했다.
- 1·2단계는 오브젝트 구성이 같고, 3단계는 누상 주자 3명(각 Visual·Body·Head)만 늘었다.
- 새 관측 크기가 직렬화됐다: 타자 16, 투수 11, 주자 57 × 4, 수비 65 × 5.
- 틀 씬·TagManager는 바뀌지 않았다.

**구종 보정.** 게임 물리를 그대로 옮긴 Python 복제로 방향·효율을 찾았다. 복제는 기존 C# 변화량과 소수점까지 일치했다. 결과는 아래와 같고, MLB 평균과의 차이는 모두 1 cm 안이다.

| 구종 | 좌우 | 상하 |
| --- | ---: | ---: |
| 포심 | -18 cm | +41 cm |
| 투심 | -38 cm | +20 cm |
| 커브 | +23 cm | -25 cm |
| 슬라이더 | +21 cm | +2 cm |
| 체인지업 | -36 cm | +18 cm |

실제 Director 투구 25개의 최대 목표 오차는 1.33 cm였다.

**회귀·규칙 (틀 씬).**

- 기존 5종 통과: `PITCH_CALL`, `BATTED_BALL`, `RUNNER`, `BATTING_EVAL`(최적 틱 20), `BATTER_REWARD`
- 새 `GameSituationVerification` 통과
  - 볼넷, 삼진, 2스트라이크 파울, 1·3루 볼넷의 만루, 만루 볼넷 득점
  - 인플레이 결과, 3아웃 반 이닝 초기화
  - 주자 오브젝트 없는 씬의 빈 베이스
  - 보상 식 예시

**단계별 결과.**

- 1·2·3단계 공통 `BatterAgentVerification`
  - 중립 타석은 존 중앙 3구 삼진, 에피소드 보상 -4.000
  - 스윙 행동은 인플레이. 1·2단계 결과 0(에피소드 0.500), 3단계 결과 +0.500(에피소드 1.000)
- 1단계: 존 중앙 포심 12구(122.5~145.8 km/h, 최대 오차 0.87 cm), 삼진 타석 4개
- 2단계
  - 볼넷 타석: 투수 -1.40 / 타자 +1.0
  - 삼진 타석: 투수 +2.50 / 타자 -4.00
  - 접촉: 투수 투구 보상 -0.616
- 3단계(무작위 상황 끔, 스크립트 수비)

  | 시나리오 | 결과 요약 | 보상 (타자 / 주자 그룹 / 수비 그룹) |
  | --- | --- | --- |
  | 중립 | 3구 삼진, 수비 에피소드 없음 | — |
  | A | 1루 `ForceOut` (3.76 s) | -0.50 / -1.00 / +1.00 |
  | B | LCF `FlyOut` (4.30 s) | — / 0 / +1.00 |
  | C | 1루 `RunnerSafe` (7.84 s, 1초 정리 시간 포함) | +0.50 / +0.25 / -0.25 |
  | D | 2루 `RunnerSafe` (10.34 s) | — / +0.50 / -0.50 |
  | E | 3B 파울 지역 `FlyOut` | — |
  | F 1루 | 2루 포스 아웃, 타자 1루 세이프(야수 선택) | 0 / -0.75 / +0.75 |
  | G 3루·1아웃 | 희생플라이 1점, 2아웃 | 0 / +1.25 / — |
  | H 만루 | 볼넷 밀어내기 1점, 만루 유지 | +1.0 / — / — |
  | I 1루·2아웃 | 삼진 → 새 반 이닝 0아웃·주자 없음 | — |
  | J 2루 | 1타점 단타 | +1.00 / +1.75 / -1.75 |
  | K 1루 | 유격수 뜬공 → 주자 리터치 전 1루 송구로 더블 아웃(`TagOut`) | — |

  결과 보상은 모두 규칙과 일치했다.

**실행 중 고친 문제.**

- **3단계 에피소드 누적 보상 불일치.** 시나리오 타구처럼 타자가 한 번도 결정하지 않은 타석에서 `EndEpisode`를 부르면, ML-Agents는 결정 없이 연속된 종료를 무시하고 누적 보상을 이어 갔다. 결정이 없던 타석은 에피소드로 닫지 않도록 고쳤다.
- **태그업 불가.** 희생플라이 시나리오에서, 포구 순간 모든 주자가 베이스에 있으면 플레이가 즉시 죽어 태그업할 수 없었다. 플레이 종료 전 1초 정리 시간(`Play Dead Seconds`)을 두었다.

**미확인 항목.**

- 원본 Editor에서 다시 불러온 뒤의 화면 동작
- Python 학습과 성능
- 무작위 상황(30%)을 켠 긴 연속 실행
- 주자 추월·한 베이스 두 주자 같은 규칙 밖 상황의 빈도

### 12.12~12.14 학습 커리큘럼 1~3단계 씬 검증 (2026-09-26)

**실행 방식.** 대화형 Editor가 원본을 열고 있어 12.10과 같은 격리 복사본에서 실행했다(긴 경로, 배치 모드). 복사본 전용 하네스가 한 번에 세 가지를 한다.

1. `TrainingSceneBuilder.Build`로 단계 씬을 만든다.
2. `BaseballPlayground`에서 기존 검증 5종을 일시정지 Play Mode로 다시 실행한다.
3. 단계 씬에서 `BatterAgentVerification`과 `TrainingStageVerification`을 실행한다.

최종 코드로 3단계 하네스를 다시 돌려 세 씬을 모두 새로 만들고, 전부 통과한 결과를 원본에 반영했다(종료 코드 0).

**반영 전 씬 차이 확인.**

- 틀 씬: 타자 Agent·Behavior Parameters·Decision Requester·`BallEye` 128줄 삭제만 있다.
- 단계 씬: 틀 씬에 해당 단계 오브젝트만 더해졌다.
- 새 스크립트 GUID는 미리 쓴 `.meta`와 씬 참조가 일치한다.
- TagManager는 바뀌지 않았다.

**회귀 (수비 없는 틀 씬, 세 번 모두 통과).** `PITCH_CALL`, `BATTED_BALL`, `RUNNER`, `BATTING_EVAL`(최적 스윙 틱 20), `BATTER_REWARD`

**12.12 1단계 (`Stage1_Batter`)**

- `PASS neutral heuristic: 2 episodes, no swing`
- `ball seen in 47/59 in-flight decisions`
- `swing action: contact` (손잡이를 존 중앙 높이로 낮추고 도착 시각 기준 스윙)
- `PASS stage 1: 12 center four-seamers, 120.3-145.8 km/h (range 120-150), arrival 0.473-0.573 s, max plate miss 0.77 cm, all called strikes, 12 plays reset, 0 aborted`

**12.13 2단계 (`Stage2_BatterPitcher`)**

- 투수 행동 매핑: 구종 5종, 좌우 ±0.466 m, 높이 0.230~1.280 m, 비유한·범위 밖 값 처리
- 회전 없는 공 대비 변화량(구속 범위 가운데, 존 중앙). 부호 조건을 모두 만족했다. 크기는 MLB 평균보다 크다([투수 Agent](pitcher-agent.md) 참고).

  | 구종 | 좌우 | 상하 |
  | --- | ---: | ---: |
  | 포심 | -17 cm | +63 cm |
  | 투심 | -52 cm | +37 cm |
  | 커브 | +19 cm | -64 cm |
  | 슬라이더 | +44 cm | -15 cm |
  | 체인지업 | -44 cm | +37 cm |

- 중립 플레이: 포심 145.0 km/h가 존 중앙에 들어가 스트라이크. 투수 +1.0, 타자 -1.0
- Director 실제 투구 25개(5구종 × 5위치): 최대 통과 오차 1.48 cm. 스트라이크 15개(+1), 볼 10개(-0.5), 보상이 일치했다.
- 접촉: 타자 +0.613, 투수 -0.613

**12.14 3단계 (`Stage3_FullTeam`)**

| 검증 | 결과 |
| --- | --- |
| 구성 | 8개 Agent: 수비 5(팀 1), 투수(팀 1), 타자·주자(팀 0) |
| 중립 투구 3개 | 수비 에피소드 없이 끝났다 |
| A 유격수 땅볼 | 3.76 s에 1루 포스 아웃. 수비 +1, 주자 -1 |
| B 좌중간 뜬공 | 4.30 s에 LCF 공중 포구 아웃 |
| C 가운데 안타 | LCF가 잡아 1루 `RunnerSafe`. 수비 -0.25, 주자 +0.25 |
| D 주자 2루 시도 | 송구가 늦어 2루 `RunnerSafe`. 수비 -0.50, 주자 +0.50 |
| E 3루 쪽 파울 뜬공 | 3B가 (-15.8, 12.9) 파울 지역에서 잡아 아웃 |
| 결과 보상 | 규칙과 일치했고, 주자 누적 보상과도 일치했다 |

**실행 중 고친 문제.**

- 틀 씬 정리에서 Decision Requester가 Agent에 의존해 지우기가 실패했다. 지우는 순서를 고쳤다.
- 검증 코드의 C# 확정 할당 오류(CS0170)를 고쳤다. 원본 Editor에도 잠깐 컴파일 오류로 보였을 수 있다.

**미확인 항목.**

- 원본 Editor에서 씬을 다시 불러온 뒤의 화면 동작
- Python `mlagents-learn` 연결과 실제 학습 성능
- 병렬 빌드(`--num-envs`)
- 수비 Agent 학습 가능성. 검증은 스크립트 수비로만 했다.
- 먼 외야 송구가 닿지 않아 거부되는 빈도

### 12.11 타자 레이 센서·중립 휴리스틱 검증 (2026-09-26)

12.10과 같은 방식으로 실행했다. 격리 복사본을 긴 경로로 열고 배치 모드에서 수정된 `Add Batter ML-Agent To Current Scene`을 두 번 호출했다.

**씬·설정 결과**

- `Actors/Batter/BallEye`에 레이 센서 하나가 생겼다. 위치는 월드 (-1.10, 1.65, 0.50)이다.
- 레이는 51개, 반각 55.6°, 길이 21.0 m, 구체 반지름 0.25 m, 스택 3이다.
- 발사 지점과 투구 목표가 부채꼴 평면에서 벗어난 거리는 둘 다 0.000 m다.
- 공에는 `Ball` 태그가 붙었고, 벡터 관측 크기는 10, `Use Child Sensors`는 켜져 있다.

**원본 반영**

- 씬 차이는 다음뿐이었다: `BallEye` 오브젝트·컴포넌트 추가, `Batter` 자식 목록 1줄, 공 태그, 관측 크기 16→10, 제거된 `heuristicSwingTimeSeconds` 줄 삭제.
- `ProjectSettings/TagManager.asset`에는 태그 `Ball`이 추가됐다. Unity가 이 파일을 serializedVersion 2→3으로 다시 저장하면서 빈 렌더링 레이어 슬롯 24개를 지웠다. 물리 레이어 32개와 이름 있는 렌더링 레이어 8개는 그대로다.
- 원본이 백업 이후 바뀌지 않았음을 확인한 뒤 씬과 TagManager를 원본에 반영했다.

일시정지한 배치 Play Mode의 `BatterAgentVerification.Run()` 결과:

- `PASS neutral heuristic: 2 episodes, no swing, last reward -1.000`
- `PASS ball eye: 51 rays x 3 values, stacks 3; ball seen in 50/64 in-flight decisions (elapsed 0.04-0.52 s)`
- `PASS swing action: contact, last reward 0.797, cumulative 0.797` (`OnActionReceived`에 0.38초 스윙 행동을 직접 넣음)

로그에 `Heuristic method called but not implemented` 경고는 없었다. `[BatterAgent]` 초기화 오류나 태그 미정의 오류도 없었다.

미확인 항목:

- 원본 Editor에서 씬·TagManager를 다시 불러온 뒤의 상태
- 비행 중 공이 보이지 않은 14회의 위치(먼 거리 레이 간격인지, 홈 통과 이후인지)
- 무작위 투구 위치 모드에서의 감지율
- 실제 학습

### 12.10 타자 Agent 배치 이동 검증 (2026-09-26)

대화형 Editor가 원본 프로젝트를 열고 있어 Unity 6000.5.2f1 격리 복사본에서 배치 모드(`-batchmode -nographics -executeMethod`)로 실행했다. 복사본 전용 하네스가 수정된 `Add Batter ML-Agent To Current Scene` 메뉴를 두 번 호출했다. 확인한 결과는 다음과 같다.

- 기존 `BaseballEnvironment/Systems/BatterAgent`가 삭제되고 `BaseballEnvironment/Actors/Batter`에 `BatterAgent`·`BehaviorParameters`·`DecisionRequester`가 하나씩 붙었다. 두 번째 호출은 변경이 없었다.
- Director 참조, `MaxStep = 0`, 관측 16·연속 7·이산 `[2]`, `Decision Period = 1`을 확인했다. 타자 자식에 센서 컴포넌트가 없음도 확인했다.
- 씬 차이는 이전 오브젝트 99줄 삭제와 `Batter`의 컴포넌트 목록·세 컴포넌트 67줄 추가뿐이었다. 삭제된 fileID를 참조하는 곳은 없었다. 이 씬을 원본에 반영했다.
- 이어서 일시정지한 배치 Play Mode에서 `BatterAgentVerification.Run()`을 실행했다. 결과는 `PASS batter ML-Agent: 2 episodes, last reward 0.800, cumulative 0.800, contact observed True`로 12.9와 같다.
- 로그에는 프로젝트 스크립트 오류가 없었다. App UI 네이티브 플러그인 `DllNotFoundException`(`-nographics`)과 PackageCache 경로 경고만 출력됐다.

실행 주의: `-projectPath`를 8.3 짧은 경로(`MRHONG~1`)로 준 세 번의 실행에서는 문제가 났다. 편집기 업데이트 약 20000틱을 기다려도 `MonoScript.GetClass()`가 null이었다. 씬의 프로젝트 MonoBehaviour는 누락 상태로 열렸고 `DecisionRequester`가 "Creating missing Agent component"를 출력했다. 같은 복사본을 긴 경로(`C:/Users/mr hong/...`)로 실행하자 30틱 만에 연결됐다. 원인은 경로 표기 차이로 추정하며, 배치 실행에는 긴 경로를 쓴다. 원본 Editor의 실시간 화면 조작과 씬 다시 불러오기 뒤 상태는 확인하지 않았다.

### 12.9 타자 ML-Agents·센서 연결 검증 (2026-09-24)

Unity 6000.5.2f1 격리 복사본에 `com.unity.ml-agents` 4.0.3을 설치하고 Editor 메뉴로 `BatterAgent`·`BehaviorParameters`·`DecisionRequester`를 씬에 연결한 뒤 저장했다. 해당 씬의 차이는 Agent 오브젝트와 Transform/컴포넌트 99줄 추가뿐임을 확인한 뒤 원본 씬에 반영했다. 패키지 잠금 파일도 복사본에서 해결한 4.0.3 버전으로 갱신했다. 4.0.0은 이 Editor에서 패키지 내부의 사용 중단 API로 컴파일 오류가 나서 사용하지 않았다.

일시 정지한 배치 Play Mode에서 `Academy.EnvironmentStep()`, Director 고정 단계, 실제 `Physics.Simulate(0.02)`를 순서대로 실행했다. 휴리스틱 기준 자세/스윙으로 **2개 에피소드가 연속 완료**, 접촉 관찰, 수동 입력·자동 반복 투구 비활성화, 마지막 보상 계산기 합계 **0.800 = ML-Agents 누적 보상 0.800**을 확인했다. 새 코드 컴파일 오류와 관측 크기 불일치 경고는 없었다. Unity Search 인덱서의 기존 `ArgumentOutOfRangeException`은 배치 시작 중 출력됐다.

수동 재실행은 Play Mode를 일시정지한 뒤 `Tools > Baseball Simulation > Verify Batter ML-Agent (Paused Play Mode)`를 선택한다. 원본 Editor의 실시간 화면 조작, Python trainer 연결, 학습된 모델의 추론과 학습 성능은 아직 검증하지 않았다.

### 12.8 타자 보상 계산 검증 (2026-09-24)

Unity 6000.5.2f1에서 원본 `Assets`/`Packages`/`ProjectSettings`를 복사한 격리 프로젝트를 배치 Play Mode로 실행했다. 원본 씬과 사용자 작업 파일은 변경하지 않았다. 새 `BatterRewardTracker`와 `BatterRewardVerification`이 컴파일됐으며 검증은 PASS였다.

| 검증 | 결과 |
| --- | --- |
| 보상식 예시 | 헛스윙 -1, 루킹 스트라이크 -1, 볼 0, 약한 페어 0.5, 빠른 페어 1.1, 높은 장타성 뜬공 -0.5, 빠른 파울 -1, 홈런 4.5 |
| 중복 지급·초기화 | 8개 에피소드 완료 이벤트 각각 1회, 보상 변화 이벤트 12회. 동일 투구 판정 반복과 `Fair` 이후 `GroundRuleDouble`에서 추가 보상 없음. `BeginEpisode`로 성분과 완료 상태 초기화 |
| 실제 `PlayDirector` 연결 | 무스윙 투구는 `CalledStrike`와 -1. 시드 12345의 최적 스윙 시점 20틱에서는 `Fair`, 접촉 0.5 + 타구 속도 0.415 = 총 0.915. 이벤트로 받은 보상 변화의 합계가 에피소드 합계와 일치 |

검증 중 Unity Search 인덱서의 `ArgumentOutOfRangeException`이 시작 단계에서 한 번 출력됐으나 보상 검증은 통과했다. 원본 Editor의 직접 Play Mode 조작, Agent 연결, 학습 실행은 이 검증에 포함되지 않았다. 수동 재실행은 Play Mode를 일시정지한 뒤 `Tools > Baseball Simulation > Verify Batter Rewards (Paused Play Mode)`를 선택한다.

### 12.7 볼/스트라이크 판정 검증 (2026-09-24)

Unity 6000.5.2f1 배치 Play Mode, 현재 원본 `Assets`(주자·펜스가 저장된 씬 포함)를 복사한 격리 프로젝트. 복사본 씬에서 `Align Strike Zone And Plate Visuals`를 실행한 뒤 일시 정지하고 `PitchCallVerification.Run()`과 기존 검증 세 개를 실제 Rigidbody로 실행했다. 컴파일 오류 0건, 검사 실패 0건.

| 검사 | 실제 결과 |
| --- | --- |
| 존 기하 | 존 중앙, 플레이트 옆·위·아래를 공 반지름만큼 스치는 경계(±1 mm), 오각형 뒤 모서리(사각형이면 스트라이크가 되는 위치가 볼), 뒤 꼭짓점 접촉 모두 기대대로 |
| 루킹 판정 | 존 중앙 → `CalledStrike`(앞 모서리 통과 높이 0.748 m). 옆 모서리를 3 cm 스침 → `CalledStrike`, 5 cm 밖 → `Ball`, 0.40 m 밖·존 위 10 cm·존 아래 10 cm → `Ball` |
| 바운드 | 높이 0 목표 → 플레이트 앞에서 바운드 후 0.085 m로 통과, `Ball` |
| 스윙 | 존 밖 0.6 m 공에 기준 시각 스윙 → `SwingingStrike`. 공이 지난 뒤 스윙 → `SwingOffered=false`, `Ball` |
| 기본 머신 투구 | 통과 높이 0.994 m, `CalledStrike`. 기준 스윙 타격 → `InPlay`(Fair) |
| 이벤트·초기화 | 11개 투구에 `PitchCalled` 정확히 11회, 초기화 후 판정·위치 비움 |
| 무작위 위치 | 설정 복제본 `RandomAroundZone`, 시드 777로 300구 → 루킹 스트라이크 비율 44.7%. 같은 시드로 다시 초기화하면 첫 목표 (0.142, 0.481) 재현 |
| 표시 정렬 | 존 틀 위 1.030 m·아래 0.480 m·폭 0.432 m, 플레이트 앞 모서리(z 0.432) 평면. 홈 표시 판 중심 z 0.216 |
| 회귀 | 타구 물리(12.6), 주루, 타자 평가 검증 모두 통과 |

미확인: 원본 씬의 존 틀 정렬(사용자가 메뉴 실행 필요), 실제 키 입력과 HUD 존 패널 가독성. 존 높이 기본값은 리그 평균이며 이 타자 도형의 키로 산정한 값이 아니다.

### 12.6 타구 판정·공기역학 확장 검증 (2026-09-23)

Unity 6000.5.2f1에서 원본 `Assets`/`Packages`/`ProjectSettings`를 복사한 격리 프로젝트를 배치 Play Mode로 실행했다. 씬에 주자와 96조각 외야 펜스를 연결하고 일시 정지한 뒤, `Physics.Simulate(0.02)`로 실제 Rigidbody를 진행했다. 검사 메뉴는 `Tools > Baseball Simulation > Verify Batted Ball Physics (Paused Play Mode)`이며, 원본 씬에도 주자와 펜스를 저장했다. 컴파일 오류와 검사 실패는 0건이다.

| 검사 | 실제 결과 |
| --- | --- |
| 36 m/s 역회전 직구 | 목표 중앙 통과 오차 0.0 mm, 목표 평면 속도 31.7 m/s |
| 타구 첫 닿음 비거리 | 90/100/110 mph, 28°: 335/381/424 ft; 100 mph, 15°/35°: 301/381 ft. 사용한 대략 기준값과 각각 25 ft 이내 |
| 페어·파울 | 외야 뜬공 페어/파울, 번트 정지 페어, 베이스 앞 파울 구름, 1루 통과 페어 모두 기대 판정 |
| 외야 경계 | 중견수 방향 공중 장외 `HomeRun`, 파울 쪽 장외 `Foul`, 펜스 맞고 복귀 `Fair`, 지면 첫 닿음 97.4 m 뒤 0.74초에 펜스 넘기 `GroundRuleDouble` |
| Director 연계 | 기준 스윙 20틱에서 고정 파워 ×1.5로 두 번 모두 42.3 m/s, 발사각 15.0°, 동일한 타구 속도 벡터. `BattedBallCalled` 발행 확인 |
| 시드 | 12345의 첫 파워 배율 1.253, 재초기화 후 동일 순서에서 재현 |
| 기존 회귀 | 주루 규칙/Director 득점, 타자 독립 평가 7종/10회 반복/스윙 범위 검증 모두 통과 |
| 결과 분포 참고 | 고정 난수 정책 300회 중 접촉 227회, `HomeRun` 26회, `GroundRuleDouble` 3회, `Timeout` 198회, 미접촉 `PassedTarget` 73회. 이 정책의 분포이며 학습 성능 지표는 아님 |

씬 안전 검사: 검증 프로젝트에서 Unity Editor가 저장한 펜스 포함 씬과 원본 씬을 `fileID` 블록별로 비교했다. 원본 851개 블록은 모두 유지됐고 기존 블록 변경은 `Field` Transform의 새 자식 참조 한 개뿐이다. 새 블록 482개는 펜스 루트 2개와 96조각 × 5개 컴포넌트다. 원본 씬 `.meta`는 교체하지 않았다.

남은 확인: 원본 대화형 Game 뷰에서 펜스·HUD의 시각 상태와 실제 키 입력은 확인하지 못했다. 배치 모드 복사본의 Unity Search 색인에서 예외가 기록됐지만 검증 구성 요소의 예외나 컴파일 오류는 없었다. 홈런·인정 2루타 결과 뒤의 자동 베이스 수여/득점, 수비·아웃/세이프는 아직 구현되지 않았다. 아래 12.5의 `BallOutOfPlay` 및 파울 미구현 기록은 당시 실행 결과이며 현재 규칙은 `environment-spec.md` 최신 절을 따른다.

### 12.5 주루 구현 검증 (2026-09-23)

실행 환경: Unity 6000.5.2f1 배치 모드(`-nographics`), **격리된 프로젝트 복사본**. 원본 프로젝트는 대화형 Editor가 열고 있어 사용하지 않았다. 복사본에서만 쓴 임시 스크립트가 씬에 `RunnerSceneSetup.AddRunner()`로 주자를 추가하고 저장한 뒤 Play Mode 진입 → 일시 정지 → `RunnerVerification.Run()`과 기존 `BattingEvaluationVerification.Run()`을 차례로 실행했다. 실제 Rigidbody 물리를 0.02초씩 진행했고 스윙 파워 난수는 고정하지 않았다.

| 검사 | 실제 결과 |
| --- | --- |
| 컴파일 | 오류 0건. 경고는 기존 코드와 같은 `FindFirstObjectByType` 사용 중단 경고(CS0618)뿐 |
| 규칙: 1루 자동 주루 | 판단 없이 1루를 밟고 멈춤. 밟은 시각 3.9404초 = (거리 - 0.30 m) / 7 m/s 기대값과 일치, 이후 1루 중심에 정지 |
| 규칙: 판단 거부 | 접촉 전, 첫 구간 귀루(목표 1루), 멈춤 중 귀루, 중복 귀루, 득점 후 판단 모두 거부 |
| 규칙: 첫 구간 진루 후 귀루 | 목표 2루 → 귀루로 1루 목표 복귀, 1루에서 멈춤 |
| 규칙: 중간 귀루·재진루 | 2루로 가다 귀루하면 1루로 돌아와 멈춤. 귀루 중 진루는 다시 2루로 방향 전환 |
| 규칙: 베이스 돌기 | 진루 두 번이면 2루를 멈추지 않고 지나(Second:Advancing) 3루에서 멈춤 |
| 규칙: 득점·초기화 | 3루에서 진루하면 Home:Scored 후 이동 정지. 초기화 후 Inactive·판단 거부 |
| Director: 전환 | 기준 스윙(18틱) 접촉 순간 주자 Advancing, 타자 렌더러 숨김, 주자 표시 활성 |
| Director: 1루 정지 | 1루 밟음 4.440초(투구 시작 기준). 공은 경계를 벗어나 정지(BallOutOfPlay=true)했지만 플레이는 BattedBallInFlight 유지 |
| Director: 진루→귀루 | 진루 60틱 후 귀루, `RunnerBaseReached` First, First 순서 |
| Director: 시간 초과 | 멈춘 주자로 12.00초에 Timeout |
| Director: 초기화 | Ready, 주자 Inactive·숨김, 타자 표시 복원, BallOutOfPlay=false |
| Director: 득점 | 임시 설정 복제본(제한 30초)에서 진루 3회 → First, Second, Third, Home 순서, 16.08초에 RunScored. 원본 설정 에셋은 바꾸지 않았다 |
| 타격 회귀 | 기존 12.4 시나리오 7개, 10회 반복, 입력 거부·초기화, 45/80/110° 기하 검사 모두 PASS. 주자가 있는 씬에서도 평가 이벤트는 투구당 1회 |

로그의 예외 두 건은 복사본 환경에서 난 것이다. 복사하지 않은 Search 색인(`SearchDatabase`)과 `-nographics`의 URP 리소스 경고이며, 프로젝트 컴포넌트의 오류나 예외는 없었다.

미확인: 원본 씬에 주자를 추가·저장하는 작업(사용자가 Editor 메뉴로 실행해야 함), 실제 F/B 키 입력, Game 뷰의 주자 외형·HUD 가독성, Scene 뷰 Gizmo(주자 선택 시 다음 베이스와 도착 반경), 다른 Fixed Timestep. 수비가 없어 아웃/세이프(R-01의 `SafeAtFirst`, D 시나리오)는 검증 대상이 아니다.

재현: 원본에서 `Tools > Baseball Simulation > Add Batter-Runner To Current Scene` 실행 후 씬 저장 → Play → Pause → `Tools > Baseball Simulation > Verify Runner (Paused Play Mode)`. 주자 참조가 없으면 규칙 검사만 하고 Director 통합은 SKIP으로 보고한다. 수동 확인: P → 약 0.36초 후 Space → 접촉 후 1루 도착 전후로 F(진루)/B(귀루), R 초기화.

### 스윙 파워 120~200% 변경 (2026-09-18 당시 기록; 최신 검증은 12.6)

스윙 시작 시 1.2~2.0배를 한 번 추첨해 기존 접촉 품질 기반 타구 속도에 곱한다. 코드 검토로 추첨 위치, 접촉 시 적용, 초기화와 평가 점수 분리를 확인했다. Unity MCP 세션에 연결되지 않아 이번 변경의 컴파일/Play Mode 검증은 미실행이다. 아래 기존 실행 기록의 타구 속도는 배율 추가 전 값이다.

재검증: 같은 자세·시각으로 P/Space/R을 반복해 ExitVelocity 크기 / Lerp(MinExitSpeed, MaxExitSpeed, ContactQuality)가 1.2~2.0인지 확인한다. 스윙마다 속력이 달라져도 접촉 여부·타이밍·네 평가값은 같아야 한다. R 후 HasSwung/HasContact=false, 속력 0인지 확인한다. 기존 Verify Batting Evaluation 메뉴로 회귀 검증한다. 외야 경계 이탈의 기대 결과는 OutOfPlay이며 홈런 판정은 없다.
### 12.4 타자 네 항목 평가 검증 (2026-09-18)

최신 기준은 `batting-evaluation.md`다. 원본 프로젝트 Unity 6000.5.2f1에서 컴파일 후 Play Mode에 진입해 일시 정지하고, `BattingEvaluationVerification.Run()`으로 실제 Rigidbody 물리를 0.02초씩 진행했다. 물리 모드와 수동 입력 옵션은 finally에서 복구했고 검증 후 Play Mode를 종료했다. 테스트 도구를 저장해 동일 절차를 Editor 메뉴로 반복할 수 있다.

| 시나리오 | 접촉 | 원시 오차 또는 결과 | 점수 (타자, 배트, 타이밍, 각도) |
| --- | --- | --- | --- |
| 기준 자세, 0.36초 스윙 | 성공 | 타이밍 -0.0135초, 각도 0° | (1, 1, 0.73, 1) |
| 타자 X -0.6m | 실패 | 손잡이도 정확히 -0.6m 이동 | (0, 1, 0.73, 1) |
| 상대 손잡이 Y +0.35m | 실패 | 타자는 기준 위치 유지 | (1, 0, 0.73, 1) |
| 0.20초 이른 스윙 | 실패 | 타이밍 -0.1735초 | (1, 1, 0, 1) |
| 0.52초 늦은 스윙 | 실패 | 타이밍 +0.1465초 | (1, 1, 0, 1) |
| 실제 스윙 평면 +40°/-20° | 성공 | 각도 오차 52.64°, 실제 타구 (9.42, -7.82, 20.13)m/s | (1, 1, 0.73, 0) |
| 스윙 안 함 | 실패 | HasTimingReference/HasClosestDistance=false | (1, 1, 0, 0) |

추가 통과: 위 7회에서 평가 이벤트 정확히 7개, 10회 반복 타격의 접촉·타이밍 일치, NaN/범위 밖 자세 거부, 투구 중 자세 변경 거부, R과 함께 요청된 스윙/자세 취소, 타자·배트·공·평가 초기화. 별도의 임시 설정 복제본으로 ±45/80/110° 합성 접촉, 120°→-60° 비대칭 스윙의 0° 도달 시간, 기준 각도를 20°/10°로 바꿨을 때 중심 타점 정렬, 접촉 1회 제한도 확인했다. 설정 원본에는 테스트 값을 쓰지 않았다.

Unity 컴파일 및 최종 Console 오류 0건. 실제 키보드 조작/HUD 가독성/Scene Gizmo 시각 확인, 다른 timestep, ML-Agents 학습 실행은 이번 검증에 포함하지 않았다. 수치 점수는 설정한 과제 기준에 대한 진단이며 학습 성능을 의미하지 않는다.

최신 타격 검증은 12.3에 기록했다. 아래 2026-09-11/12 표의 Play Mode 미실행 및 타격 미구현 설명은 당시 기록이다. H-01/H-02의 전체 야구 결과·주루·수비 기대값은 여전히 후속 단계이고, 현재 타격 부분의 검증만 12.3에서 구분한다.

### 12.3 타격 구현 검증 (2026-09-18)

스윙 범위 조절 추가 검증: 설정 에셋의 임시 복제본으로 +45/-45, +80/-80, +110/-110도를 적용했다. Unity의 실제 Direction 메서드에서 시작/종료 방향 변화를 확인했고, 세 범위 모두 중앙 교차 합성 입력 접촉이 true였다. 시작 -80/끝 +80의 역방향 설정은 TryValidate가 거부했다. 원본 기본값 +80/-80 유지, Console 오류 0건. 이 검사는 합성 입력 검사이며 실제 키 입력/물리 재실행은 하지 않았다.

후속 수정: 사용자가 반대 방향 스윙을 확인해 배트 Y축 회전을 +80° → -80°로 수정했다. Unity에서 실제 Direction 메서드 결과는 준비 (0.17, 0, -0.98), 종료 (0.17, 0, +0.98)로 확인했다. 중앙 교차 합성 입력을 Tick에 넣은 접촉 검사는 true, 타구 속도는 (0, 8.05, 30.03)m/s였다. 컴파일 오류 0건이고 씬의 배트 준비 자세도 갱신·저장했다. 아래 실제 물리 테스트 수치는 회전 방향 수정 전 기록이며, 이 후속 검사는 합성 입력 검사로 실제 물리 재실행과 구분한다.

실행 환경: 원본 프로젝트의 Unity 6000.5.2f1, 기존 BaseballPlayground 씬, 기본 설정. Unity MCP로 Play Mode 진입 후 일시 정지하고 Physics.simulationMode를 Script로 임시 전환했다. 매 단계 Director.FixedUpdate를 호출한 뒤 실제 Physics.Simulate(0.02)를 실행했다. 검증 후 물리 모드를 원래 값으로 복구하고 Play Mode를 종료했다. 난수는 사용하지 않았다.

| 검사 | 실제 결과 |
| --- | --- |
| 스크립트 컴파일 | Unity 컴파일 통과, 최종 Console 오류 0건 |
| 씬 연결 | Editor API로 타자·배트·Director 참조를 추가하고 기존 씬 저장 |
| Ready 대기 | 공 (0, 1.80, 18.44), 속력 0 |
| 정상 스윙 | 투구 루프 18번째 틱에 (0°, 15°) 요청, 접촉 이벤트 1회, 속도 (0, 6.36, 23.74)m/s, 품질 0.6907008 |
| 이른 스윙 | 2번째 틱 요청, 접촉 0회, PassedTarget |
| 늦은 스윙 | 28번째 틱 요청, 접촉 0회, PassedTarget |
| 우측 타구 | 18번째 틱 (+25°, 15°), 접촉 1회, 속도 (10.03, 6.36, 21.52)m/s |
| 좌측 높은 타구 | 18번째 틱 (-25°, 35°), 접촉 1회, 속도 (-8.51, 14.10, 18.25)m/s |
| 타구 관찰 종료 | 위 정상 타격 모두 Timeout으로 종료, 목표 뒤 통과 조건에 잘못 종료되지 않음 |
| 반복 타격·초기화 | 10/10 정상 접촉, 초기화 후 Ready·접촉 false·스윙 false·속력 0 |
| 중복/예약 요청 | 같은 투구 18/20번째 틱 중복 스윙 거부, R과 함께 요청한 스윙은 초기화가 취소 |

수동 재현: 씬을 열고 Play → P → 약 0.36초 후 Space. 방향키로 좌우·발사각을 바꾼 뒤 R로 초기화하여 반복한다. 스크립트는 RequestThrowPitch / RequestSwing(new SwingCommand(0f, 15f)) / RequestResetPlay를 사용한다. 일반 실행에서는 자동 Unity 물리를 사용하며 위 Script 모드 전환은 검증에만 필요하다.

미확인: 실제 키보드 입력, Game 뷰 외형과 HUD 가독성, 여러 Fixed Timestep 및 접촉 가장자리 경계의 오차, 씬 재개방 후 시각 검증. 이전 피칭머신의 0.02m 중앙 통과 오차 조건은 이번 테스트에서 재측정하지 않았다. 전체 H-01의 Miss 결과와 H-02의 RunnerDefense/주루/수비는 미구현이므로 통과로 기록하지 않는다.

## 1. 현재 검증 상태

이 문서는 대부분 구현 후 수행할 **계획된 검증 절차**다. 2026-09-11 기준으로 단계 1(야구장과 기본 배치)이 구현됐고(실행 기록 12.1장), 2026-09-12에 단계 2를 피칭머신(직구 하나, 중앙 통과, 재투구, 초기화) 범위로 부분 구현했다(실행 기록 12.2장).

아래 표에 없는 시나리오(H, R, D, E, X, I)와 PM-02~06은 모두 `미실행`이며 통과로 간주하면 안 된다. P-01/P-02는 피칭머신 범위로 배치 모드에서 부분 확인했다(12.2장).

| 범위 | 현재 상태 | 근거 |
| --- | --- | --- |
| 프로젝트 파일 조사 | 완료 | Unity/패키지/씬/스크립트/설정 파일을 읽어 문서에 반영 |
| Unity 컴파일 | 실행함 | `Assembly-CSharp`, `Assembly-CSharp-Editor` 컴파일 오류 없음 (배치 모드, 격리된 프로젝트 복사본) |
| 씬 생성·로드 | 실행함 | `BaseballPlayground.unity`를 Editor API로 생성하고 다시 열어 확인 (단계 1은 원본 프로젝트, 피칭머신은 격리된 복사본) |
| V-00 좌표와 배치 | 부분 통과 | 좌표·거리·페어/경계 질의와 화면 표시를 확인. 대화형 Editor의 Scene 뷰 Gizmo는 미확인 (12.1장) |
| PM-01 탄도 계산(수학적 검증) | 실행함 | `PlayDirector.TryComputeLaunchVelocity`를 30/36/42 m/s에 대해 이상적 운동학으로 역산해 중앙 통과 오차가 부동소수점 수준(0.0000 m)임을 확인 (12.2장). **Rigidbody 실제 물리 비행은 미확인** |
| Unity Editor(대화형) 실행 | 미실행 | 작업 중 Editor가 원본 프로젝트를 점유하고 있어 배치 모드(격리된 복사본)로만 확인함 |
| Play Mode | 미실행 | P/R 키 입력, HUD 표시, 실제 Rigidbody 비행·CCD 충돌, 자동 반복 타이밍은 대화형 Play Mode에서만 확인 가능 |
| 수동 시나리오 | 미실행 | `ManualPlayController`는 존재하나 실제 키 입력은 대화형 세션에서만 확인 가능 |
| 스크립트 시나리오 | 해당 없음 | 사용자가 수동 조작(P/R 키)과 Inspector 자동 반복만 요청했고 `ScriptedPlayController`는 요청 범위 밖이다 |
| 초기화 반복 | 미실행 | `PlayDirector.RequestResetPlay()` 로직은 구현했으나 Play Mode에서 연속 실행·키 연타는 미확인 |

## 2. 검증 기록 규칙

각 실행은 다음 정보를 남긴다.

- 실행 날짜, Unity 버전, 실행 플랫폼
- 시나리오 ID와 수동/스크립트 모드
- 사용한 설정 에셋, 변경한 설정값, 시드
- 명령의 시뮬레이션 시각과 주요 인자
- 관측한 상태 전이, 최종 결과, 핵심 이벤트 순서
- Console 오류·경고 유무
- 통과/실패와 증거 스크린샷 또는 로그 위치

같은 시드가 입력 조건 재현에 도움을 주는지는 확인하되 서로 다른 운영체제·하드웨어·Unity/물리 엔진 버전 사이의 궤적이 비트 단위로 같다는 검증 항목은 두지 않는다. 논리 결과가 다르면 원인을 조사하고, 미세한 부동소수점 차이만 있으면 허용 오차와 실행 환경을 기록한다.

## 3. 공통 준비

1. Unity `6000.5.2f1`에서 프로젝트를 연다.
2. `BaseballPlayground.unity`를 열고 Console을 지운 뒤 컴파일 오류가 없는지 확인한다.
3. `DefaultBaseballEnvironment` 설정 에셋을 선택하고 기본값이 `environment-spec.md`와 일치하는지 확인한다.
4. Main Camera, Directional Light, Global Volume, FieldLayout, Ball, Batter, Runner, Ball Fielder, First Baseman, PlayDirector, 입력 컨트롤러, DebugPresenter 참조를 확인한다.
5. 활성 입력 모드를 하나만 선택한다. 먼저 Scripted 모드로 조건을 고정하고, 필수 시나리오가 통과한 뒤 Manual 모드로 대표 시나리오를 반복한다.
6. Scene 뷰 Gizmo에서 좌표축, 파울선, 경계, 베이스 경로, 포구·수신·도착 반경을 켠다.
7. Game 뷰 HUD에서 상태, 시간, 시드, 공 위치/속도, 소유자, 주자 목표, 결과가 보이는지 확인한다.

구체적인 키는 구현 시 HUD와 이 문서에 함께 기록한다. 검증 절차는 특정 키 이름보다 `ThrowPitch`, `Swing`, `SetRunnerTarget`, `ThrowTo`, 일시 정지, 초기화라는 환경 동작을 기준으로 한다.

## 4. 기본 배치와 투구

### V-00 좌표와 배치

**초기 조건**

- Play Mode 진입 전 기본 설정
- 홈 `(0, 0, 0)`, 1루 `(19.40, 0, 19.40)`, 2루 `(0, 0, 38.79)`, 3루 `(-19.40, 0, 19.40)`, 투수 `(0, 0, 18.44)`

**조작**

1. Scene 뷰에서 각 표식의 Transform과 거리 Gizmo를 확인한다.
2. Play Mode에 진입하되 투구하지 않는다.

**기대 결과**

- +Z가 중견수 방향, +X가 1루 방향이다.
- 홈-1·3루 거리는 약 `27.43 m`, 홈-투수 거리는 `18.44 m`다.
- 상태는 `Ready`, 시간은 0, 공 속도는 0이다. (피칭머신 범위에는 아직 결과·주자·배터가 없다. `PlayDirector.State`가 `Ready`이고 HUD의 완료한 투구 수가 0이면 된다.)
- 카메라에서 홈, 공, 피칭머신, 스트라이크존 표시를 식별할 수 있다.

### P-01 정상 직구 (피칭머신 범위로 수정)

**초기 조건**

- 기본 설정(투구 속력 `36 m/s`)
- 투구 시작 `(0, 1.80, 18.44)`, 목표(스트라이크존 중앙) `(0, 1.00, 0.50)`
- 자동 반복 꺼짐

**조작**

1. Play Mode에서 P 키로 `ThrowPitch`를 한 번 실행한다.
2. 공이 목표 평면을 지나 투구가 끝날 때(`Ended`)까지 관찰한다.

**기대 결과**

- 상태가 `Ready → PitchInFlight → Ended`로 한 번씩 전환된다.
- 공의 초기 속도 크기는 `36 m/s`이고 방향은 목표(스트라이크존 중앙) 쪽으로, Unity 중력에 따라 낙하하며 위치·속도가 HUD에 갱신된다.
- HUD의 중앙 통과 오차가 표시되고 `0.02 m` 이내다. 이전·현재 고정 위치의 교차점 계산 덕분에 빠른 공도 값이 비어 있지 않아야 한다.
- 종료 사유는 `PassedTarget`이다(목표를 지나 여유 거리만큼 더 나아가 끝남).
- 같은 투구가 중복 실행되지 않는다(비행 중 P를 다시 눌러도 무시되고 거부 사유가 HUD/Console에 남는다).

### P-02 투구 조건 변경 (피칭머신 범위로 수정)

**초기 조건**

- P-01과 같되 `BaseballEnvironmentConfig`의 `pitchSpeed`를 `30 m/s`, `42 m/s`로 각각 바꿔 실행

**조작**

1. 각 조건 사이에 R 키로 초기화한다.
2. 같은 목표로 투구하고 비행 시간과 중앙 통과 오차를 기록한다.

**기대 결과**

- 속력이 높을수록 목표 도달(비행) 시간이 더 짧다(이상적 계산 기준 `30/36/42 m/s` → 약 `0.599/0.498/0.427 s`, 12.2장 참고).
- 두 속력 모두 중앙 통과 오차가 `0.02 m` 이내다.
- 변경 전 속력이나 통과 오차가 다음 초기화 후 잘못 남지 않는다.
- 모든 실행은 `Ended`로 정상 종료되고 Console 예외가 없다.

### PM-01 기본 설정 10회 반복 중앙 통과

**초기 조건**

- 기본 설정(`36 m/s`), 자동 반복 꺼짐

**조작**

1. P → (자연 종료 대기) → R → P 순서를 10회 반복하거나, Inspector의 자동 반복을 켜고 간격 `1~2 s`로 줄여 10회 관찰한다.

**기대 결과**

- 10회 모두 중앙 통과 오차가 `0.02 m` 이내로 기록된다.
- 공은 하나만 존재하며 매회 재사용된다(새 Ball 오브젝트가 늘지 않는다).
- 반복 사이에 남은 속도나 위치 오차로 인한 궤적 변화가 없다.

### PM-02 비행 중 초기화

**초기 조건**

- P로 투구해 공이 비행 중인 상태

**조작**

1. 공이 목표 평면을 지나기 전에 R을 누른다.

**기대 결과**

- 다음 고정 시간 단계에서 공이 즉시 투구 시작점으로 되돌아가고 선속도·각속도가 0이 된다.
- 상태가 `Ready`로 전환되고 중앙 통과 오차·종료 사유 표시가 지워진다.
- 이전 비행에서 예정돼 있던 사건(뒤늦은 목표 통과 등)이 새 상태에 영향을 주지 않는다.

### PM-03 연속 초기화와 키 연타

**초기 조건**

- 임의 상태(`Ready`/`PitchInFlight`/`Ended`)

**조작**

1. R을 빠르게 여러 번 누른다.
2. `Ended` 상태에서 P를 여러 번 연타한다.
3. 마지막에 P-01을 다시 실행한다.

**기대 결과**

- 중복 Ball 생성, 중복 발사, 잔존 속도가 없다.
- `Ended`에서의 P 연타는 매번 거부되고(Ready 상태가 아님) 첫 정상 투구와 같은 논리로 이어진다.
- Console 예외가 없다.

### PM-04 자동 반복 투구

**초기 조건**

- Inspector에서 `PlayDirector`의 자동 반복을 켜고 간격을 기본값 `3 s`로 둔다.

**조작**

1. P로 첫 투구를 실행한 뒤 개입하지 않고 관찰한다.

**기대 결과**

- 투구가 끝난(`Ended`) 뒤 약 `3 s` 후 공이 자동으로 초기화되고 다시 발사된다.
- 수동 P/R과 같은 명령 경로(`RequestThrowPitch`/`RequestResetPlay`)를 사용하므로 결과가 수동 실행과 논리적으로 같다.
- 자동 반복 중 R을 누르면 대기 중인 재투구가 취소되고 `Ready`로 즉시 전환된다.

## 5. 스윙과 타격

### H-01 헛스윙

**초기 조건**

- 기본 직구
- 공이 접촉 중심에 도달하기 `0.20 s` 이상 전에 스윙

**조작**

1. 투구 후 이른 `Swing`을 한 번 실행한다.
2. 공이 홈 뒤 판정면을 지날 때까지 기다린다.

**기대 결과**

- `SwingStarted`는 기록되지만 `BallBatContact`는 없다.
- 공 속도는 배트 때문에 바뀌지 않는다.
- 결과는 `Miss` 하나이며 스윙을 했다는 이유로 별도 삼진/스트라이크 카운트가 생기지 않는다.

### H-02 정상 페어 타격

**초기 조건**

- 기본 직구, 스윙 타이밍 중심
- 좌우 각 `0°`, 발사각 `15°`

**조작**

1. 투구 후 중심 시각에 `Swing`을 실행한다.
2. 접촉 직전/직후 공 속도와 사건을 기록한다.
3. 첫 지면 접촉까지 관찰한다.

**기대 결과**

- `BallBatContact`가 한 번 발생하고 상태가 `RunnerDefense`로 전환된다.
- 공 속도는 타구 방향으로 한 번만 바뀌며 `8–32 m/s` 범위의 계산 결과를 따른다.
- 공의 지면 투영 위치가 `z >= abs(x)`를 만족하면 페어다.
- 타자주자와 수비가 활성화된다.

### H-03 좌우·발사각 영향

**초기 조건**

- H-02와 같은 접촉 시각
- 실행 A: 좌우 `+25°`, 발사 `5°`
- 실행 B: 좌우 `-25°`, 발사 `35°`

**조작**

1. A를 실행하고 타구 초기 벡터를 기록한 뒤 초기화한다.
2. B를 같은 시드로 실행해 초기 벡터를 기록한다.

**기대 결과**

- A는 1루 쪽(+X)의 낮은 타구, B는 3루 쪽(-X)의 높은 타구다.
- 각 명령값이 HUD/이벤트에 기록된다.
- 접촉 검출과 타구 속도 적용은 실행마다 한 번이다.

### H-04 파울

**초기 조건**

- 접촉 가능한 타이밍
- 좌우 각 또는 배치가 첫 지면 접촉을 `z < abs(x)` 영역에 만들도록 설정

**조작**

1. 투구와 스윙을 실행한다.
2. 첫 지면 접촉 또는 파울 쪽 경계 이탈까지 관찰한다.

**기대 결과**

- 공중 포구가 없으면 `Foul` 하나로 종료한다.
- 볼카운트는 만들지 않고 자동 재투구도 하지 않는다.
- 파울 지점과 판정 근거가 이벤트/디버그 표시에 남는다.

## 6. 주루

### R-01 1루 도착

**초기 조건**

- 페어 타격 시나리오
- 최종 목표 1루, 수비 포스가 생기지 않도록 수비 자동 처리를 끄거나 멀리 배치

**조작**

1. 유효 타격을 실행한다.
2. 주자의 다음 목표와 위치를 관찰한다.

**기대 결과**

- 접촉 전에 주자는 비활성이며 접촉 직후 타자주자 1명만 활성화된다.
- 주자는 홈에서 1루까지 선분 경로로 이동한다.
- 도착 반경 진입 시 `BaseReached(First)`가 한 번 발생하고 1루에 정지한다.
- 포스가 먼저 없었으므로 결과는 `SafeAtFirst`다.

### R-02 홈 도착과 득점

**초기 조건**

- 최종 목표 홈인 득점 검증 모드
- 수비에 의한 아웃이 발생하지 않게 설정

**조작**

1. 유효 타격 후 주자를 계속 진행시킨다.
2. 베이스 도착 사건 순서를 기록한다.

**기대 결과**

- 도착 순서가 First → Second → Third → Home이다.
- 중간 베이스에서는 플레이가 종료되지 않는다.
- 홈 도착에서 `RunScored` 이벤트와 결과가 한 번 발생한다.
- 이 결과는 전체 경기 점수판이나 이닝을 만들지 않는 단일 플레이 결과다.

## 7. 수비와 소유권

### D-01 공중 포구 아웃

**초기 조건**

- 수비수가 도달 가능한 위치로 향하는 페어 뜬공
- 첫 지면 접촉 전 공 높이가 포구 높이 이하가 되는 경로

**조작**

1. 타격 후 수비 자동 이동을 실행한다.
2. 포구 순간 공 지면 접촉 이력, 소유자, 상태를 기록한다.

**기대 결과**

- 수비수는 공의 현재 위치 또는 표시된 간이 낙하지점으로 이동한다.
- 첫 지면 접촉 전에 포구 반경·높이 조건이 성립한다.
- 소유자가 해당 수비수로 한 번 바뀌고 `FlyOut` 하나로 종료한다.
- 주자가 같은 틱에 베이스에 도착해도 FlyOut이 우선한다.

### D-02 1루 세이프

**초기 조건**

- 페어 땅볼
- 주자 속도 또는 수비 배치를 조절해 주자가 먼저 1루에 도착하게 함

**조작**

1. 타격, 수비 포구, 1루 송구를 실행한다.
2. `BaseReached(First)`와 1루 수비수의 소유+점유 성립 시각을 비교한다.

**기대 결과**

- 주자 도착 시각이 더 이르다.
- 결과는 `SafeAtFirst` 하나다.
- 이후 도착한 송구가 결과를 `ForceOutAtFirst`로 바꾸지 않는다.

### D-03 1루 포스 아웃

**초기 조건**

- 페어 땅볼
- 수비수와 송구 조건을 조절해 1루 수비수가 먼저 공을 소유하게 함

**조작**

1. 타격, 포구, 송구를 실행한다.
2. 1루 소유+점유와 주자 도착 시각을 비교한다.

**기대 결과**

- 1루 수비의 유효 포스 조건이 먼저 성립한다.
- `OutRecorded`와 `ForceOutAtFirst` 결과가 한 번씩 기록된다.
- 주자의 나중 도착은 결과를 세이프로 바꾸지 않는다.

### D-04 같은 틱의 1루 경합

**초기 조건**

- 속도와 시작 위치를 조절해 주자 도착과 유효 포스가 같은 고정 틱에 발생

**조작**

1. 스크립트 모드로 동일 시드를 최소 3회 실행한다.
2. 두 사건의 고정 틱과 순번, 최종 결과를 기록한다.

**기대 결과**

- 같은 틱 단순화 규칙에 따라 `ForceOutAtFirst`가 선택된다.
- 실행 순서나 콜백 순서에 따라 결과가 번갈아 바뀌지 않는다.
- 최종 결과 이벤트는 한 번이다.

### D-05 송구 후 공 소유권 이동

**초기 조건**

- 타구 처리 수비수가 땅볼을 소유
- 1루 수비수가 수신 위치에 있음

**조작**

1. 소유 수비수에게 `ThrowTo(FirstBaseman)`를 실행한다.
2. 송구 직전, 비행 중, 수신 직후의 스냅샷을 저장한다.

**기대 결과**

- 직전: Ball Fielder만 소유자다.
- 비행 중: 소유자는 없고 공 Rigidbody가 활성이다.
- 수신 직후: First Baseman만 소유자다.
- 소유권 변경은 각 전환당 한 번이고 두 수비수가 동시에 소유하지 않는다.

## 8. 종료 예외와 초기화

### E-01 경기 영역 이탈

**초기 조건**

- 장외가 되도록 높은 속도 또는 경계 쪽 타구를 설정

**조작**

1. 페어 외야 수평 경계, 상단, 하단 조건을 각각 제어된 시나리오로 실행한다.
2. 경계를 넘은 틱의 위치와 결과를 기록한다.

**기대 결과**

- 페어 외야/상하 경계 이탈은 `OutOfPlay` 하나로 종료한다.
- 홈런, 자동 득점, 다음 타자 처리는 만들지 않는다.
- 파울 쪽 이탈은 `Foul`로 분류되어 페어 장외와 구분된다.

### E-02 제한 시간 종료

**초기 조건**

- 제한 시간을 `0.5 s`로 낮춤
- 그 전에 미타격, 접촉, 포구, 장외, 정지 판정이 일어나지 않도록 공을 아직 비행 중인 조건 사용

**조작**

1. 플레이를 시작하고 HUD 타이머를 관찰한다.
2. 제한 시간을 넘길 때까지 다른 명령을 내리지 않는다.

**기대 결과**

- `PlayTimedOut`이 한 번 발생하고 결과는 `Timeout`이다.
- 시간 초과 뒤 공과 주자 동작이 새 결과를 만들지 않는다.
- 초기화하면 제한 시간과 타이머가 시나리오 설정에 맞게 복원된다.

### E-03 공 정지

**초기 조건**

- 지원되는 세이프/아웃으로 이어지지 않게 수비와 주자를 비활성화한 제어 시나리오
- 공이 지면에서 `0.15 m/s` 미만으로 `1.0 s` 유지

**조작**

1. 낮은 속도로 공을 지면에 보내 정지 시간을 관찰한다.

**기대 결과**

- 임계 속도에 잠깐 진입했다 벗어나면 타이머가 다시 시작한다.
- 연속 유지 시간을 충족하면 `DeadBall` 하나로 종료한다.
- 정지와 Timeout이 같은 틱이면 DeadBall이 우선한다.

### X-01 플레이 도중 초기화

**초기 조건**

- 공이 투구 비행 중이거나 주자·수비가 이동 중

**조작**

1. 초기화 명령을 내린다.
2. 다음 고정 시간 단계와 `Ready` 복귀 후 스냅샷을 확인한다.

**기대 결과**

- 예약 명령과 이전 사건이 지워진다.
- 모든 객체가 초기 Transform으로 복귀한다.
- 공 속도·각속도 0, 소유권 대기 기본값, 주자 비활성, 스윙/포구 플래그 없음, 타이머 0, 결과 없음이다.
- 이전 플레이의 늦은 충돌 콜백이 새 플레이를 종료하지 않는다.

### X-02 소유 상태와 종료 상태 초기화

**초기 조건**

- 실행 A: 수비수가 공을 소유
- 실행 B: 임의 결과로 `Ended`

**조작**

1. 각 상태에서 초기화한다.
2. 바로 새 정상 직구 시나리오를 실행한다.

**기대 결과**

- 기존 소유자와 결과가 제거된다.
- 새 투구가 정확히 한 번 시작되고 이전 이벤트가 재발행되지 않는다.
- 두 경우 모두 같은 초기 스냅샷을 만든다.

### X-03 연속 초기화

**초기 조건**

- `Ready`, `PitchInFlight`, `RunnerDefense`, `Ended` 상태를 각각 준비

**조작**

1. 각 상태에서 초기화 명령을 연속 10회 요청한다.
2. 마지막에 P-01과 H-02를 실행한다.

**기대 결과**

- 예외, 중복 객체, 중복 구독, 누적 속도, 위치 오차가 없다.
- 초기화 완료 이벤트는 실제 처리 횟수 정책에 맞게 중복 없이 기록된다.
- 후속 투구와 타격 결과가 첫 실행과 같은 논리 흐름을 따른다.

## 9. 입력 모드 통합

### I-01 수동 모드 한 플레이

**초기 조건**

- ManualPlayController만 활성

**조작**

1. UI/HUD에 기록된 수동 조작으로 초기화, 투구, 각도 조절, 스윙, 송구를 수행한다.
2. 결과 확인 후 다시 초기화한다.

**기대 결과**

- HUD의 활성 입력원이 Manual이다.
- 입력은 Director 명령으로 수락/거부되고 상태에 맞지 않는 입력은 이유와 함께 무시된다.
- 투구부터 결과와 초기화까지 학습 모델 없이 완료된다.

### I-02 스크립트 모드 반복

**초기 조건**

- ScriptedPlayController만 활성
- H-02 또는 D-03 시간표와 고정 시드

**조작**

1. 같은 설정과 시드로 최소 5회 실행하고 매번 초기화한다.

**기대 결과**

- 명령 시각, 계산된 타구 벡터, 이벤트 순서, 논리 결과가 기록상 일치한다.
- 물리 위치에는 정한 허용 오차를 적용한다.
- 실행할수록 구독자 수나 사건 수가 증가하지 않는다.

### I-03 입력원 충돌 방지

**초기 조건**

- 두 컨트롤러가 모두 활성화되도록 잘못 설정

**조작**

1. Play Mode에 진입해 투구 입력을 시도한다.

**기대 결과**

- 시스템이 한 입력원만 선택하거나 플레이 시작을 차단하고 명확한 오류를 표시한다.
- 같은 프레임에 공이 두 번 발사되지 않는다.

## 10. 설정 변화 회귀 확인

필수 시나리오가 기본값으로 통과한 뒤 다음 값을 한 번에 하나씩 바꾸고 P-01, H-02, R-01, D-01, D-03, X-01을 반복한다.

| 변경 항목 | 확인할 영향 | 변하면 안 되는 항목 |
| --- | --- | --- |
| 투구 속도 `30/42 m/s` | 도달 시각과 낙하량 | 중복 투구, 상태 전이 순서 |
| 스윙 좌우 각 `±25°` | 타구 X 방향 | 접촉 횟수 |
| 발사각 `5/35°` | 뜬 정도와 낙하지점 | 주루 경로 순서 |
| 주자 속도 `5/9 m/s` | 1루 도착 시각과 세이프/아웃 경쟁 | 베이스 위치 |
| 수비 속도 `4/8 m/s` | 포구 도달 가능성 | 공 소유권 단일성 |
| 포구/수신 반경 | 경계 안팎 성공 여부 | 결과 중복 |
| 시작 배치 | 이동 거리와 결과 | 좌표축 정의 |
| 시드 | 무작위 조건 선택 | 명령 API와 지원 규칙 |

값을 바꿔도 환경은 지원 결과 중 하나로 종료하거나 유효성 검사에서 시작을 막아야 하며, 멈춘 채 방치되거나 NaN 위치가 되어서는 안 된다.

## 11. 전체 통과 기준

- 필수 시나리오 V-00, P-01~02, PM-01~04, H-01~04, R-01~02, D-01~05, E-01~03, X-01~03, I-01~03을 실제 실행하고 기록했다.
- 각 플레이는 최종 결과가 하나이고 Console 예외가 없다.
- 수동과 스크립트 입력이 같은 상태 전이·환경 이벤트 경계를 사용한다.
- 변경된 설정에서도 지원되는 결과 또는 명확한 입력 오류로 끝난다.
- 연속 초기화 뒤 이전 위치, 속도, 소유권, 결과, 타이머, 이벤트가 남지 않는다.
- 문서의 현재 지원/미지원 규칙과 실제 구현이 일치한다.
- 학습 모델, 보상, ML-Agents 연결 없이 검증된다.

실행하지 않은 항목은 통과 표시하지 않고 이유와 다음 확인 방법을 남긴다.

## 12. 실행 기록

### 12.1 단계 1 — 야구장과 기본 배치 (2026-09-11)

**실행 환경**

- Unity `6000.5.2f1`, Windows 10, URP `17.5.0`
- 실행 방식: Unity Editor **배치 모드**(`-batchmode -executeMethod`). 작업 시점에 대화형 Editor가 원본 프로젝트를 점유하고 있어, 원본과 같은 `Assets`/`Packages`/`ProjectSettings`로 만든 임시 프로젝트에서 씬을 생성하고 결과 에셋을 원본으로 옮겼다.
- 사용한 설정 에셋: `Assets/BaseballSimulation/Config/DefaultBaseballEnvironment.asset` (기본값, 변경 없음)
- 시드: 단계 1에는 난수원이 없다.

**통과한 항목**

| 확인 | 결과 |
| --- | --- |
| 컴파일 | `Assembly-CSharp`, `Assembly-CSharp-Editor` 오류 0건 |
| 씬 로드 | 최상위 4개(`BaseballEnvironment`, `Main Camera`, `Directional Light`, `Global Volume`), GameObject 197개(필드 65 + 관중석 132) |
| 씬 참조 무결성 | 로컬 `fileID` 참조 전부 해석됨, 끊어진 참조 0건 |
| 홈 | `(0.00, 0.00, 0.00)` |
| 1루 / 2루 / 3루 | `(19.40, 0, 19.40)` / `(0, 0, 38.79)` / `(-19.40, 0, 19.40)` |
| 투수판 / 투구 시작 / 투구 목표 | `(0, 0, 18.44)` / `(0, 1.80, 18.44)` / `(0, 1.00, 0.50)` |
| 홈-1루, 홈-3루 거리 | 각 `27.4357 m` (명세 약 27.43 m) |
| 홈-투수판 거리 | `18.4400 m` |
| 홈-2루 거리 | `38.7900 m` |
| 페어 판정 `IsFairGroundPoint` | 중앙·양 파울선 위·1·3루 베이스는 페어, 파울 영역과 홈 뒤는 파울. 7개 경계 조건 통과 |
| 경계 판정 `IsInsidePlayBoundary` | 수평 `109.9/110.1 m`, 상단 `59.9/60.1 m`, 하단 `-1.9/-2.1 m` 안팎 구분. 8개 경계 조건 통과 |
| 주루 경로 `GetNextBase` | 홈→1루→2루→3루→홈 |
| 설정 검증 `TryValidate` | 기본 설정은 통과. 1루를 -X로, 투구 목표 높이를 음수로 바꾼 설정은 사유와 함께 거부 |
| 카메라 프레이밍 | 홈, 1·2·3루, 투수판, 투구 시작·목표점과 대략적인 수비 위치 2곳이 모두 뷰포트 안 |
| 지면 Collider | 씬 전체 Collider 1개(`Ground` MeshCollider). 홈, 페어 `z=60`, 파울 `x=-40`, 홈 뒤 `z=-20`에서 모두 `y=0.000` 명중 |
| 필드 치수 | 페어 원판 반경 `110.0 m`(경계와 일치), 내야 흙 한 변 `27.4357 m`, 안쪽 잔디 한 변 `21.0357 m`(베이스 패스 폭 `3.20 m`), 마운드 반경 `2.74 m`, 홈 원 반경 `3.96 m` |
| 화면 표시 | 카메라 렌더를 오프스크린으로 받아 확인. 페어 영역이 홈에서 90도로 열려 원형 경계까지 이어지고, 내야 흙·베이스 패스·마운드·홈 원·파울선·경계 표식이 구분됨 |
| 관중석 | 경계 밖 `114~146 m`에 4단 128조각. Collider 0개(씬 전체 Collider는 여전히 `Ground` 1개), 경기 영역 침범 없음 |
| 기존 에셋 | `Assets/Scenes/SampleScene.unity`와 기존 `.meta` GUID 변경 없음 |

API 단정 21개 모두 통과, 실패 0건.

**실행하지 못한 항목과 이유**

| 항목 | 이유 | 다음 확인 방법 |
| --- | --- | --- |
| 대화형 Editor에서 씬 열기 / Console 경고 확인 | 작업 중 Editor가 프로젝트를 점유해 배치 모드로만 실행함 | Unity에서 `BaseballPlayground.unity`를 열고 Console 확인 |
| Scene 뷰 Gizmo(축, 베이스 경로, 파울선, 경계, 거리 라벨) 표시 | Gizmo는 Editor 렌더링에서만 그려짐 | Scene 뷰에서 Gizmos를 켜고 확인 |
| 대화형 Game 뷰에서 눈으로 확인 | 오프스크린 렌더로 색상·영역 구분은 확인했으나 Editor Game 뷰 자체는 열지 못함 | Game 뷰에서 확인 |
| 씬을 닫았다 다시 열어 참조 유지 확인 | 위와 같음 | 씬을 다시 열고 `Field`의 `FieldLayout` 참조 확인 |
| 배치 모드에서 스크립트 참조 유지 | Unity 배치 모드는 사용자 어셈블리의 MonoScript-클래스 연결을 만들지 않아(프로젝트의 모든 스크립트가 동일) 생성 직후 `m_Script`가 빈 참조로 저장된다. 저장된 씬과 설정 에셋의 해당 참조를 실제 스크립트 GUID로 맞춘 뒤 참조 무결성을 다시 확인했다 | 대화형 Editor에서 메뉴로 다시 생성하면 이 보정이 필요 없다 |

**오프스크린 렌더 시 주의**

배치 모드에서 카메라를 직접 렌더해 확인할 때 두 가지에 걸렸고, 둘 다 씬 문제가 아니라 측정 방법 문제였다.

- 선형 색 공간 프로젝트라 렌더 타깃을 `RenderTextureReadWrite.sRGB`로 만들지 않으면 PNG 색이 어긋난다.
- 배치 모드의 **첫 렌더 한 장**은 화면 전체가 뭉개진 단색으로 나온다. 워밍업으로 한 장 버리고 나면 후처리를 켜도 정상이다. 처음에는 `Global Volume`의 후처리 때문이라고 의심했지만, 같은 시점에서 후처리를 켜고 끄며 다시 찍어 확인한 결과 후처리와 무관했다. `SampleSceneProfile`은 그대로 쓴다.

**재현 방법**

1. Unity에서 프로젝트를 연다.
2. `Tools > Baseball Simulation > Build Playground Scene`으로 씬을 다시 만든다.
3. `Tools > Baseball Simulation > Validate Playground Scene`으로 기준점과 실측 거리를 Console에서 확인한다.

### 12.2 단계 2 일부 — 피칭머신 (2026-09-12)

**실행 환경**

- Unity `6000.5.2f1`, Windows 10, URP `17.5.0`
- 실행 방식: Unity Editor **배치 모드**(`-batchmode -nographics -quit -executeMethod`). 작업 시점에 대화형 Editor(`Unity.exe`, PID 확인됨)가 원본 프로젝트를 이미 열어 두고 있어, 그 세션과 충돌·손상을 피하기 위해 원본과 같은 `Assets`/`Packages`/`ProjectSettings`(스크립트·`.meta` 포함)를 별도 임시 폴더에 복사해 그 복사본에서만 빌드·검증했다. **원본 프로젝트의 씬·에셋은 배치 모드로 다시 만들거나 덮어쓰지 않았다** — `.cs` 소스 파일만 원본에 직접 작성했다.
- 사용한 설정 값: `BaseballEnvironmentConfig` 기본값(투구 속력 `36 m/s`, 목표 뒤쪽 여유 `3 m`, 제한 시간 `5 s`)
- 시드: 피칭머신에는 난수원이 없다(직구 하나, 항상 같은 조건).

**통과한 항목**

| 확인 | 결과 |
| --- | --- |
| 컴파일 | `BallController`, `PlayDirector`, `ManualPlayController`, `DebugPresenter`, 갱신된 `SimulationContracts`/`BaseballEnvironmentConfig`/`BaseballPlaygroundBuilder` 포함 전체 재컴파일, 오류 0건(4회 배치 실행 모두) |
| 씬 생성 | `BaseballPlayground.unity`에 `Actors/Ball`, `Systems/PlayDirector`, `Systems/ManualPlayController`, `Systems/DebugPresenter`, `Field/PitchOrigin/PitchingMachine`(Stand+Body+Muzzle), `Field/PitchTarget/StrikeZoneVisual`(Top/Bottom/Left/Right/Center) 생성 확인 |
| 컴포넌트 존재 | `PlayDirector`, `BallController`, `ManualPlayController`, `DebugPresenter` 모두 씬에서 검출됨 |
| 기준 좌표(회귀) | 홈/1루/2루/3루/투수판/투구 시작·목표점, 홈-1·3루 거리(`27.4357 m`), 홈-투수판 거리(`18.44 m`) 모두 단계 1과 동일하게 유지됨(회귀 없음) |
| **탄도 계산(수학적 검증)** | `PlayDirector.TryComputeLaunchVelocity(origin, target, speed, gravity)`가 반환한 초기 속도를 등가속도 운동학(`x(t) = origin + v·t + 0.5·g·t²·`)으로 직접 역산해 목표 평면 도달 시점의 위치를 계산했다. `30/36/42 m/s` 세 속력 모두 계산된 속도 크기가 요청 속력과 정확히 일치하고, 목표까지의 거리(중앙 통과 오차)가 `0.0000 m`(부동소수점 수준)였다. 목표 `0.02 m` 이내를 여유 있게 만족한다. 비행 시간은 각각 `0.599 s`, `0.498 s`, `0.427 s`로 속력이 빠를수록 짧아지는 기대와 일치했다 |
| 발사 방향 | 세 속력 모두 낮은 발사각(빠르고 낮은 탄도) 해가 선택됨을 코드 경로로 확인(판별식의 두 해 중 작은 `tan θ` 선택) |

**중요한 주의 — 이 항목은 실제 Rigidbody 물리 검증이 아니다.** 위 탄도 계산 검증은 `TryComputeLaunchVelocity`가 반환한 벡터를 **순수 수학(등가속도 운동학)으로 역산**한 것이며, Unity `Rigidbody`가 실제로 그 속도를 받아 고정 시간 간격(`0.02 s`)으로 적분하면서 생기는 이산화 오차, 충돌, 연속 충돌 검사(CCD) 동작은 검증하지 못했다. "물리 시간 간격에 따른 오차도 실제 실행으로 확인한다"는 요구는 **미충족**이며 대화형 Play Mode에서 직접 확인해야 한다.

**실행하지 못한 항목과 이유**

| 항목 | 이유 | 다음 확인 방법 |
| --- | --- | --- |
| P/R 키 입력(Play Mode) | 배치 모드는 `Update()`/`FixedUpdate()`가 프레임 단위로 도는 대화형 실행을 지원하지 않음 | Unity Editor에서 `BaseballPlayground.unity`를 열고 Play 버튼으로 진입해 P/R 키를 직접 눌러 확인 |
| Rigidbody 실제 비행·CCD 충돌 | 위와 같음(고정 시간 간격 물리 스텝은 Play Mode에서만 진행됨) | Play Mode에서 공이 스트라이크존을 실제로 통과하는지, HUD의 중앙 통과 오차가 `0.02 m` 이내인지 확인 |
| 기본 설정 10회 반복(PM-01) | 위와 같음 | Play Mode에서 P → 자동 종료 대기 → R → P를 10회 반복하거나 자동 반복을 켜고 관찰 |
| 속력 변경 30/42 m/s 실제 물리 확인(P-02) | 위와 같음. 수학적 계산만 확인됨(위 표) | Play Mode에서 `BaseballEnvironmentConfig.pitchSpeed`를 바꿔 가며 확인 |
| 비행 중 초기화(PM-02) | 위와 같음 | Play Mode에서 투구 직후 R 입력 |
| 연속 초기화·키 연타(PM-03) | 위와 같음 | Play Mode에서 R/P 연타 |
| 자동 반복 투구 타이밍(PM-04) | 위와 같음 | Play Mode에서 자동 반복을 켜고 간격 경과 관찰 |
| Game 뷰 HUD·Scene 뷰 시각 확인 | 배치 모드는 `OnGUI`를 렌더링하지 않고 Game 뷰 자체가 없음 | Play Mode에서 좌상단 HUD와 씬의 피칭머신·스트라이크존 표시를 눈으로 확인 |
| `설정 데이터(BaseballEnvironmentConfig)` 참조 검증 | **실제로 사용자의 라이브 프로젝트에서도 재현됐다** — 처음에는 격리된 배치 복사본에서만 관찰된 문제로 추정했으나 잘못된 판단이었다. 근본 원인을 재현·격리해 확인했다: `BaseballEnvironmentConfig.cs`에 필드를 추가해 재컴파일한 직후 `AssetDatabase.LoadAssetAtPath<BaseballEnvironmentConfig>`가 "No script asset for BaseballEnvironmentConfig" 내부 경고와 함께 존재하는 에셋도 못 찾거나, `ScriptableObject.CreateInstance`로 새로 만든 순간 네이티브 쪽이 파괴된 객체를 돌려주는 경우가 있다. 이 객체는 `config.HomePosition`처럼 필드를 직접 읽는 호출은 정상 동작하지만(관리되는 C# 필드는 살아있음), 다른 컴포넌트의 참조 필드에 대입하면 `{fileID: 0}`(빈 참조)으로 직렬화된다 — Transform 참조(`home`, `firstBase` 등)는 씬에 이미 속한 오브젝트라 이 문제가 없어 그동안 드러나지 않았다. 배치 모드(`-batchmode -executeMethod`)에서는 이 상태가 최대 5초 재시도(0.25 s × 20회)로도 해소되지 않아, 배치 모드 자체가 이 문제를 완전히 재현하기 좋은 환경(응답을 기다리는 동안 Unity의 일반 업데이트 루프가 돌지 않음)이지만 인터랙티브 세션에서도 스크립트를 재컴파일한 직후 곧바로 메뉴를 실행하면 같은 증상이 나타날 수 있음을 확인했다 | 코드에 다음 방어 조치를 적용했다: (1) `FieldLayout`/`BallController`에 `AssignConfig()`를 추가해 `SerializedObject` 왕복 대신 필드에 직접 대입, (2) 에셋을 불러올 때 최대 20회(0.25 s 간격) 재시도, (3) 그래도 연결에 실패하면 씬을 저장하지 않고 명확한 예외로 빌드를 중단(예전처럼 `{fileID: 0}`으로 조용히 저장하지 않음). **그래도 실패하면**: Unity Editor에서 컴파일이 완전히 끝난 뒤(Console 하단 스피너가 사라진 뒤) `Tools > Baseball Simulation > Build Playground Scene`을 다시 실행하거나, Editor를 완전히 재시작한 뒤 실행한다. 그래도 안 되면 `Field`(FieldLayout)와 `Actors/Ball`(BallController)의 Config 필드에 `DefaultBaseballEnvironment`를 Inspector에서 직접 드래그해 연결한다(항상 통하는 수동 대안) |

**재현 방법**

1. Unity에서 `BaseballPlayground.unity`를 연다(이미 대화형 Editor에 열려 있다면 그대로 사용). Console 하단에 컴파일 스피너가 없는지 확인한다.
2. `Tools > Baseball Simulation > Build Playground Scene`으로 피칭머신을 포함해 씬을 다시 만든다. 예외가 뜨면(위 표 참고) 메뉴를 한 번 더 실행하거나 Editor를 재시작한다.
3. `Field`의 Config, `Actors/Ball`의 Config, `Systems/PlayDirector`의 FieldLayout/Ball 참조가 채워져 있는지 Inspector로 확인한다. 비어 있으면 수동으로 드래그해 연결한다.
4. Play 버튼으로 진입해 P/R 키로 위 표의 미실행 항목을 직접 확인한다.
