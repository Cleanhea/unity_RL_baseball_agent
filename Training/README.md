# 학습 실행 안내

## 타자 카메라 192×192·12장 (2026-10-09)

사용자 요청으로 현재 학습 씬의 타자 카메라를 **192×192 흑백·12장·100Hz**로 변경한다. 최근 영상 범위는110ms이며 기존256×256·30장 대비 영상 원소 수가77.5% 줄어든다. 기존30장 학습은 정상 저장했고, 새 입력에 맞춘 타자 가중치와 기존 투수·주자·CF 가중치를 별도 초기값으로 보존한다. 타자 이식은 근사 초기화이므로 기존 타격 성능을 그대로 보장하지 않는다. 새 run-id와 새 Player를 사용한다. 현재 계약·실행·검증은 [타자 Camera12_192](../docs/batter-camera12-192.md)를 따른다. 아래30장·6장 기록은 이전 설정이다.

## 영상 프레임 재사용·수동 3단계 직행 (2026-10-09)

사용자 요청으로 30장·해상도·픽셀 값을 유지한 PNG 프레임 캐시와 흑백 배열 변환 최적화를 적용한다. 기존 타자 모델을 별도 보존해 준비·2단계를 생략하고 수동 명령으로3단계에 직접 사용할 수 있다. 투수·주자·CF는 새로 학습하며 기존 성적 통과로 간주하지 않는다. 기존 자동 연결은 취소했다. [계약·검증·첫 실행/재개 명령](../docs/batter-visual-cache-stage3.md)을 따른다.

## 타이밍 집중·정타 진행 보상 강화 (2026-10-09)

Camera30 타자는 배트 위치·각도를 고정한 타이밍 과정부터 높이·각도·배트 위치를 차례로 열고 전체 준비로 연결한다. 몸 고정·30장·100Hz와 최종 정타·성적 통과 기준은 유지한다. 준비 과정 및 `batter_prepared=1`의 정타 미달 실제 페어 보상 상한을 0.5→1.5로 높이고 접촉 품질 75%·속도 25%로 진행 보상을 강화한다. [최신 계약·수식·실행](../docs/batter-timing-progress.md)을 따른다. 아래 약한 페어 0~0.5 기록은 변경 전 설명이며 준비 모드가 아닌 기존 학습에는 계속 적용한다.

## 타자 몸 위치 고정 (2026-10-09)

학습 타자의 몸 위치는 모든 과정에서 기준 위치로 고정한다. 기존 모델을 이어 쓰도록 행동 크기를 유지하며 몸 이동 행동 0·1만 무시한다. 배트 위치·스윙 각도·타이밍·30장 입력·성적 기준은 유지한다. [계약·학습 재개·검증](../docs/batter-fixed-stance.md)을 따른다. 아래 몸 이동 관련 기록은 변경 전 설명이다.

## 영상 30장·100Hz 적용 (2026-10-09)

현재 타자 영상 스택은 6→30장으로 변경했다. 256×256 흑백과 10ms 촬영/판단을 유지하며, 투구 중 연속 관측의 시간 범위는 290ms다. 기존 6장 모델의 1,062,459스텝 가중치를 별도 30장 초기화 모델로 옮기고, 전체 준비 과정 1에서 새 학습을 시작했다. Camera30 설정은 타자 배치 32·버퍼 256·궤적 64를 사용한다. 기존 6장 설정·실행 파일·학습 결과는 보존했다. [입력·모델 이식·메모리·실행 안내](../docs/batter-camera-30.md)를 따른다. 아래 6장 기록은 이전 검증 결과다.

## 타자 센서의 공만 렌더하는 빌드 (2026-10-09)

현재30장 전환의 `Training/builds/BatterCamera30/BatterCamera30.exe`, `Training/builds/Stage2_Camera30/Stage2_Camera30.exe`, `Training/builds/Stage3_Camera30/Stage3_Camera30.exe`에 공만 렌더하는 센서 설정을 포함한다. 기존6장 Player는 보존한다. [관측 계약·적용·검증](../docs/batter-ball-only.md), [30장 실행·모델 이식](../docs/batter-camera-30.md)을 따른다.

## 현재 학습을 유지한2→3단계 자동 연결 (2026-10-09)

별도 `continue_stage3.py` 대기 프로세스를 현재 분리 준비→전체 준비→2단계 체인에 연결했다.2단계 타자/투수의 학습 한도 도달·최종 모델 저장·전체 준비 통과·6장 계약을 모두 확인한 뒤 `stage3_full_team_camera6_selfplay.yaml`과 `Stage3_Camera6.exe`로3단계를 시작한다. 상태는 `results/stage23_camera6_chain/continuation-status.json`이다. [현재 연결·실행·취소·검증](../docs/stage23-continuation.md)을 따른다. 아래3장 일반 커리큘럼 명령은 기존 경로다.

## 타이밍부터 분리하는6장 학습 (2026-10-09)

현재 모델을 정상 저장해 실제 Unity 고정 평가를 수행하고 선택적 타이밍→높이→각도→위치 준비 과정을 추가했다. 실행은 `prepare_batter.py`와 `batter_skills_camera6.yaml`의 `--skills` 모드다. 성적 기준을 유지하며 단계별로 제어를 열고 마지막에는 기존16개 준비를 이어간다. [실행·평가·재개 안내](../docs/batter-skills.md)를 따른다.

## 영상 6장·촬영/판단 100Hz (2026-10-08)

사용자 요청으로 256×256 흑백 영상 스택을3→6장, 고정 물리·타자 촬영/판단 간격을20→10ms로 바꿨다. 과거 영상 범위는40→50ms이며 수비·주자 간격과 플레이 제한 시간은 기존 초 단위를 유지한다. 기존1단계 가중치를 동일 시점 채널에 이식한 별도 `stage1_batter_camera6_prepare` 실행을 사용한다. 아래3장/50Hz 설명은 이전 기록이다. [최신 계약·이식·실행·검증](../docs/batter-camera-timing.md)을 따른다.

## 2단계 타자 준비 과정 (2026-10-08)

현재 권장 실행은 Training/prepare_batter.py와 config/batter_preparation.yaml이다. 새 run-id는 stage1_batter_camera_prepare이며 --continue-stage2는 성적 조건 통과·정상 저장 후 stage2_batter_pitcher_prepared_selfplay로 자동 연결한다. 아래 일반 커리큘럼 명령은 기존 경로다. [계약·실행·검증](../docs/batter-preparation.md)을 따른다.

## 1단계 정타·타구 속도 재학습 (2026-10-08)

`Training/resul/stage1_batter_camera_contact`의 마지막 287만 스텝 요약은 접촉률79.15%·인플레이 타석97.17%지만 타구 속도60.80 km/h·발사각−21.94°·첫 낙하거리1.29 m였다. 접촉 추가 보상이 약한 타구에도 계속 붙어 평균 보상+1.66을 받았다. 접촉 추가분은 첫 과정에서만 주고 중앙 정타 과정을 추가했다. 기존 결과는 보존하며 새 `stage1_batter_camera_power` 실행을 사용한다.

Unity Play를 정지하고 컴파일 완료 후 `Stage1_Batter` 씬을 다시 연다. 이전 학습기가 실행 중이면 그 터미널에서 Ctrl+C를 한 번 누르고 저장 완료를 기다린다. 현재 코드는 실행 중인 Python 학습기의 YAML을 바꾸지 않는다.

```powershell
cd C:\MainScreen\Dev\GitDirectory\unity_RL_baseball_agent
conda activate mlagents
mlagents-learn Training/config/stage1_batter.yaml --run-id=stage1_batter_camera_power --results-dir=Training/results
```

학습기가 기다리면 Unity에서 Play한다. 과정 번호와 보상 목표가 달라 이전 실행을 `--resume`하지 않는다. 이전 내려치기 모델을 초기값으로 가져오지 않고 새 실행으로 검증한다. 이 작업에서 장시간 학습을 시작하지는 않았다.

과정은 **중앙 접촉0 → 중앙 정타1 → 제한 자세·위치2 → 전체 타격3**이다. 첫 과정에서만 실제 접촉 +1.5를 추가하며 파울이면 회수해 순보상−1이다. 다음 과정은 품질≥0.6·속도≥120 km/h·발사각5~35°인 페어 타구에 보상≥2.63, 다른 페어 타구는 최대0.5다. 0→1 전환은 최소100타석·보상 평균>0.2, 이후는 최소100타석·평균>1.0이다. 모든 과정의 구속은120~150 km/h이며 자동 스윙은 없다. [타자 계약](../docs/batter-agent.md#1단계-접촉-우선-커리큘럼-2026-10-08)을 따른다.

TensorBoard에서 `Batted Ball/Contact Quality`, `Exit Speed (km/h)`, `Launch Angle (deg)`, `Batter Training/Qualified Hit`를 함께 확인한다. 자동2단계 전환은 최종 과정3·스텝 한도·모델 저장에 더해 최근 전체 조작 요약3개의 정타 타석 비율 평균≥25%를 요구한다. 첫 낙하거리 지표는 굴러간 거리와 다르다. 3단계 미해결 시간 초과는 진루 보상을 주지 않으며 `Env/Fielding Timeout`으로 감시한다.

## 3단계 단순 수비로 새 학습 (2026-10-07)

저장된 Stage3 씬은 경기장 4개 × 수비 몸 5개(C·1B·2B·3B 고정, CF만 이동), 수비 Agent는 경기장마다 1개다. CF는 포구 다음 고정 단계에 새 관측으로 1·2·3루에 바로 던진다. 보유 대기·홈·공 운반·중계는 없다. 먼 외야에서 베이스에 도달하지 못하는 송구는 환경이 속력을 보충한다. 기존 수비 `[5]` 모델은 새 `[3]` 행동과 호환되지 않으므로 새 run-id로 시작한다.

타자는 포수 카메라로 1단계부터 재학습한다. `python Training/auto_curriculum.py`는 `stage1_batter_camera_power` → `stage2_batter_pitcher_camera` → `stage3_full_team_camera_cf`를 이어간다. 셀프플레이의 3단계는 `stage3_full_team_camera_cf_selfplay`다. 기존 실행 파일을 재빌드한다(`--skip-build` 제외). 저장된 씬을 원본 Editor에서 다시 열어 적용한다.

수비 설정은 batch 128 / buffer 2048 / max_steps 3,000,000이며 단일 Agent 기준의 초기값이다. `stage3_full_team_resume.yaml`은 새 camera_cf 실행만 재개한다. `stage3_full_team_refield.yaml`은 새 camera_cf 타자·투수·주자를 가져오고 CF만 새로 학습한다. 아래 9명·ray 모델의 run-id와 재개 명령은 과거 기록이며 현재 씬에 사용하지 않는다. 새 모델의 학습 성능은 아직 확인하지 않았다.

## 포수 시점 카메라로 새 학습 시작 (2026-10-07)

세 단계의 타자 센서는 256×256 흑백 영상 3프레임이다. 기존 레이 타자 체크포인트/ONNX와 호환되지 않으므로 기존 run을 `--resume`하거나 `--initialize-from`으로 읽지 않는다. 이전 결과는 보존한다. 아래 과거 run-id와 재개 절은 이전 레이 학습 기록이며 현재 카메라 실행에는 적용하지 않는다.

1단계부터 Editor에서 학습하려면 ML-Agents Python 환경에서 저장소 루트 기준으로 실행한다.

```powershell
mlagents-learn Training/config/stage1_batter.yaml --run-id=stage1_batter_camera_power --results-dir=Training/results
```

Unity 컴파일이 끝난 뒤 저장된 `Stage1_Batter` 씬을 다시 열고 Play한다. 모든 경기장에 `Actors/Batter/CatcherEye`와 `Field/BatterCatcherCamera`가 있어야 한다. 자동 실행은 `python Training/auto_curriculum.py`로 1→2→3단계를 새 run-id로 이어간다. 실행 파일은 재빌드하고, 기존 레이 실행 파일에 `--skip-build`를 사용하지 않는다. 시각 입력은 그래픽이 필요하므로 `--no-graphics`를 넣지 않는다.

- 일반 run-id: `stage1_batter_camera_power`, `stage2_batter_pitcher_camera`, `stage3_full_team_camera_cf`
- 셀프플레이: 1단계 공유, 2·3단계 run-id 뒤에 `_selfplay`. `python Training/auto_curriculum.py --self-play`
- 타자 CNN: `vis_encode_type: simple`; 영상 메모리를 위해 `batch_size=128`, `buffer_size=2048`. 1단계는 위 접촉 커리큘럼과 `beta=0.02`, `beta_schedule=constant`, `summary_freq=10000`을 적용한다.
- 고정 타자 평가: 새 1단계 최종 `BaseballBatter.onnx`를 Unity의 `Assets/BaseballSimulation/Models/BenchmarkBatter_Stage1_Camera.onnx`로 가져온 뒤 2·3단계 씬에서 `Tools > Baseball Simulation > Training > Update Batter Camera Sensors In Current Scene`을 실행하고 저장한다. 모델이 준비될 때까지 고정 타자 평가는 꺼지고 기준 스크립트 투수 평가는 유지된다. 자동 실행의 첫 빌드에는 이 새 모델이 없으므로 고정 타자 평가를 쓰려면 1단계 종료 후 모델을 가져와 `--start-stage 2`로 다시 빌드한다.
- 검증 메뉴: 그래픽이 켜진 Editor의 Play Mode를 일시정지하고 `Tools > Baseball Simulation > Verify Batter ML-Agent (Paused Play Mode)`.



세 단계 커리큘럼의 설계와 씬 구성은 [docs/training-curriculum.md](../docs/training-curriculum.md)에 있다. 이 폴더에는 ML-Agents 학습 설정 파일이 있다. Unity가 가져오지 않도록 `Assets` 밖에 두었다.

| 단계 | 씬 | 설정 | run-id |
| --- | --- | --- | --- |
| 1 타자 | `Scenes/Training/Stage1_Batter` | `config/stage1_batter.yaml` | `stage1_batter_camera_power` |
| 2 타자+투수 | `Scenes/Training/Stage2_BatterPitcher` | `config/stage2_batter_pitcher.yaml` | `stage2_batter_pitcher_camera` |
| 3 전체 | `Scenes/Training/Stage3_FullTeam` | `config/stage3_full_team.yaml` | `stage3_full_team_camera_cf` |
| 2 셀프플레이 | 2단계와 같음 | `config/stage2_batter_pitcher_selfplay.yaml` | `stage2_batter_pitcher_camera_selfplay` |
| 3 셀프플레이 | 3단계와 같음 | `config/stage3_full_team_selfplay.yaml` | `stage3_full_team_camera_cf_selfplay` |

셀프플레이 설정은 타자와 투수가 번갈아 배운다. 한쪽이 배우는 동안 상대는 과거 스냅샷으로 고정된다. 3단계의 주자·수비는 셀프플레이 없이 계속 배운다. 값과 이유는 [동시 학습과 셀프플레이](../docs/training-curriculum.md#동시-학습과-셀프플레이)에 있다.

## 준비 (한 번만)

Unity 패키지 `com.unity.ml-agents` 4.0.3은 Python 패키지 `mlagents` 1.1.0과 짝이다. 이 패키지는 Python 3.10.1~3.10.12만 지원한다. 3.11 이상은 설치되지 않는다. 이 PC에는 `py -3.10`(3.10.9)이 있다.

```
py -3.10 -m venv C:\mlagents-venv
C:\mlagents-venv\Scripts\activate
python -m pip install --upgrade pip
pip install torch~=2.2.1 --index-url https://download.pytorch.org/whl/cpu
pip install mlagents==1.1.0
pip install psutil
mlagents-learn --help
```

- 위 새 가상 환경에서 CPU로 학습하려면 `--torch-device cpu`를 쓴다. 이미 실행 중인 1단계 체크포인트에 붙을 때는 **그 학습과 같은 PyTorch 장치**를 사용해야 한다. 자동 실행 스크립트는 기본적으로 ML-Agents의 자동 선택을 따르며, 필요하면 `--torch-device cpu` 또는 `--torch-device cuda`로 지정한다.
- `grpcio` 빌드 오류가 나면 `pip install grpcio==1.48.2`를 먼저 설치한 뒤 다시 설치한다.
- 자동 실행 스크립트는 `mlagents-learn`, `tensorboard`, `psutil`, `PyYAML`이 들어 있는 같은 Python 환경에서 실행한다.

단계 씬은 저장소에 이미 있다. 틀 씬을 바꿨다면 `Tools > Baseball Simulation > Training > Build Stage N Scene`으로 다시 만든다.

## 세 단계 자동 실행 (저장소 루트에서)

```
python Training/auto_curriculum.py
```

스크립트가 원본 Editor와 분리된 임시 프로젝트에서 단계별 Windows 실행 파일을 `Training/builds/`에 만든다. 1 → 2 → 3단계 학습기를 차례로 실행하고, 각 단계의 **모든 Behavior가 YAML의 `max_steps`에 도달**해 최종 모델·체크포인트가 저장됐는지 확인한 뒤 다음 단계로 넘어간다. 추가로 1단계 `run_logs/training_status.json`의 `batter_lesson.lesson_num`이 3(`FullBatting`)이고 최근 전체 조작 요약3개의 정타 타석 비율 평균이25% 이상이어야 2단계로 넘어간다. 준비 난이도에 머문 모델은 스텝 한도에 도달해도 전환하지 않는다. 이는 타격 성능 보장은 아니다. 기존 결과 디렉터리를 덮어쓰지 않으며 빌드·학습 실패 시 다음 단계로 넘어가지 않는다. 각 단계는 자기 씬이 들어 있는 실행 파일을 사용하므로 Editor에서 씬을 바꿀 필요가 없다.

빌드는 격리 프로젝트의 에셋 가져오기와 스크립트 컴파일을 먼저 마친 뒤 별도 Unity 실행에서 진행한다. 단계 씬에 누락된 스크립트 참조가 있으면 실행 파일을 만들기 전에 중단한다. 가져오기 로그는 `Training/results/curriculum-import.log`에 남는다.

격리 프로젝트는 시스템 임시 폴더의 **긴 이름 경로**(예: `C:\Users\mr hong\AppData\Local\Temp\baseball-curriculum-build`)에 만든다. 이 PC의 TEMP는 8.3 짧은 이름(`C:\Users\MRHONG~1\...`)으로 잡혀 있다. 그 경로로 Unity를 열면 프로젝트 스크립트가 하나도 연결되지 않은 실행 파일이 만들어진다. 그러면 Agent가 없어 `mlagents-learn`이 `UnityTimeOutException`으로 끝난다. 반대로 경로가 너무 길면 Burst 빌드가 실패한다.

이미 Editor에서 새 `stage1_batter_camera_power` 1단계를 학습 중이면, 해당 `mlagents-learn`의 PID를 확인한 다음 다음 명령을 별도 터미널에서 실행한다. 현재 학습이 끝날 때까지 기다렸다가 2·3단계를 실행한다. 이전 `stage1_batter_camera`·`stage1_batter_camera_contact` 실행에 붙이는 용도로 쓰지 않는다.

```
python Training/auto_curriculum.py --attach-stage1-pid <PID>
```

빌드가 이미 끝났으면 `--skip-build`를 붙일 수 있다. `--build-only`는 실행 파일만 만든다. 상태는 `Training/results/curriculum-status.json`, 빌드 로그는 `Training/results/curriculum-build.log`, 단계별 학습 로그는 `Training/results/<run-id>.log`에 남는다. 현재 run-id의 결과가 이미 있으면 중단하므로 재학습할 때는 기존 결과를 보존한 채 새 실행 계획을 준비해야 한다.

1단계를 수동으로 완료한 뒤 2·3단계 자동 학습을 이어가려면 `python Training/auto_curriculum.py --start-stage 2`를 실행한다. 스크립트는 1단계 체크포인트·최종 모델·TensorBoard 스텝을 확인하고 새 실행 파일을 빌드한 다음 2단계부터 시작한다. 2단계까지 완료한 경우 `--start-stage 3`도 쓸 수 있다. 기존 실행 파일이 현재 코드·씬으로 빌드됐다고 확인된 경우에만 `--skip-build`를 붙인다.

### 셀프플레이로 실행

`--self-play`를 붙이면 2·3단계가 `*_selfplay.yaml` 설정과 `*_selfplay` run-id를 쓴다. 1단계는 공유한다. 예를 들어 1단계가 끝난 상태에서 2단계부터 셀프플레이로 이어 가려면 다음을 실행한다.

```
python Training/auto_curriculum.py --self-play --start-stage 2
```

- 동시 학습 결과(`stage2_batter_pitcher` 등)와 폴더가 달라 서로 덮어쓰지 않는다.
- 3단계 셀프플레이는 2단계 **셀프플레이** 결과를 `init_path`로 읽는다.
- 한 번에 한 팀만 배우므로 투구 수로 동시 학습의 약 1.3배가 걸린다.
- TensorBoard의 `Self-play/ELO`는 이 환경에서 믿지 않는다. `Matchup/*`과 `Benchmark Batter/*`·`Benchmark Pitcher/*`를 본다. 이유는 [커리큘럼 문서](../docs/training-curriculum.md#동시-학습과-셀프플레이)에 있다.

## 개별 단계 수동 실행

```
mlagents-learn Training/config/stage1_batter.yaml --run-id=stage1_batter_camera_power --results-dir=Training/results
```

"Start training by pressing the Play button"이 나오면 Unity Editor에서 해당 단계 씬을 열고 Play한다. 자동 실행과 별도로 수동 학습할 때 쓴다. 2·3단계 설정은 이전 단계 결과(`Training/results/<run-id>/<Behavior>/checkpoint.pt`)를 `init_path`로 읽는다. 그래서 1 → 2 → 3 순서로, 위 run-id 그대로 학습한다. run-id를 바꾸면 설정 파일의 `init_path`도 고친다.

### 기존 2단계 학습 재개

`--resume`은 **타자와 투수 각각의** `Training/results/stage2_batter_pitcher/<Behavior>/checkpoint.pt`를 읽는다. 두 파일이 모두 있어야 한다. 설치된 ML-Agents 1.1.0은 `--resume` 중에도 YAML의 `init_path`가 있으면 그 경로를 우선 읽으므로, 2단계 신규 학습용 설정 대신 `config/stage2_batter_pitcher_resume.yaml`을 쓴다. 두 설정의 학습 값은 같고 재개용 설정에는 타자의 `init_path`만 없다. 결과 폴더를 백업한 다음 저장된 스텝을 확인하고 실행한다.

```
mlagents-learn Training/config/stage2_batter_pitcher_resume.yaml --run-id=stage2_batter_pitcher --results-dir=Training/results --resume
```

학습기가 Editor 연결을 기다리면 `Stage2_BatterPitcher` 씬을 Play한다. `--force`는 기존 결과를 덮어쓰므로 복구에 사용하지 않는다. 종료할 때는 Ctrl+C를 한 번 누르고 **두 Behavior의 저장 완료 메시지**가 나올 때까지 기다린다. 투수의 `checkpoint_interval`은 20,000스텝이므로 그 이전에 프로세스가 강제로 종료되면 재개용 파일이 없을 수 있다. `auto_curriculum.py`는 결과가 이미 있는 run-id의 자동 재개를 지원하지 않는다.

- 진행 그래프: `tensorboard --logdir Training/results`
  - 보상·손실 외에 야구 지표도 나온다: 삼진·볼넷률, 스윙·컨택률, 타구 속도·발사각, 구종 비율, 3단계 플레이 결과 등.
  - 목록과 뜻은 [TensorBoard 지표](../docs/training-curriculum.md#tensorboard-지표)에 있다.
  - 2단계부터는 한쪽을 고정한 평가 타석의 성적이 `Benchmark Batter/*`·`Benchmark Pitcher/*`로 따로 나온다. 상대가 함께 바뀌는 보상 곡선과 달리 실력 변화를 같은 잣대로 본다. [고정 상대 평가](../docs/training-curriculum.md#고정-상대-평가-2026-09-27) 참고.
  - 같은 값이 모든 Behavior 로그에 들어가므로 `BaseballBatter` 쪽을 보면 된다.
  - 이 지표가 들어가기 전에 만든 `Training/builds/` 실행 파일에는 지표가 없다. `--skip-build` 없이 다시 빌드한다.
- 학습된 모델 사용: `Training/results/<run-id>/<Behavior>.onnx`를 해당 Agent의 Behavior Parameters `Model`에 넣는다.

### 3단계 수비수가 서 있거나 조금만 움직일 때

수비 Agent는 **타구가 살아 있는 동안에만** 0.1초마다 이동·송구를 결정한다. 투구 중, 볼넷·삼진·헛스윙에는 수비 결정을 요청하지 않는다. 학습기를 연결했더라도 새 수비 모델이 처음부터 공을 쫓는 것은 아니다. 초기 무작위 행동은 방향이 자주 바뀌고 이동 가속도도 제한되어 있어, 전체 구장 화면에서는 움직임이 작게 보일 수 있다.

2026-09-30 저장된 `stage3_full_team`을 확인했을 때 타자는 9,272스텝, 투수는 250스텝이었지만 수비는 320스텝, 주자는 64스텝이었다. 수비·주자의 optimizer 상태에는 업데이트가 한 번도 없었다. 타자·투수는 이전 단계 모델에서 시작하고, 수비·주자는 새 모델에서 시작한다. 타격이 드물면 수비 학습도 늦어진다.

3단계 일반·셀프플레이 설정의 수비 `buffer_size`를 20,480→4,096, `batch_size`를 1,024→256, `summary_freq`를 50,000→2,000으로 조정했다. 학습 데이터 수집 대기와 지표 표시 간격을 줄이는 변경이며, 수비 실력 향상을 검증한 값은 아니다. 관측·행동·보상과 경기 진행 방식은 같다. 연결 중인 학습기에는 YAML 수정이 즉시 적용되지 않으므로 저장 후 재개해야 한다.

기존 일반 3단계 실행을 이어갈 때는 다음 설정을 쓴다. 타자·투수의 `init_path`를 제거해 **네 Behavior 모두 자기 3단계 체크포인트**를 읽는다. 새 학습용 설정에 `--resume`만 붙이면 설치된 ML-Agents 1.1.0은 타자·투수를 다시 2단계에서 가져오므로 주의한다.

```powershell
mlagents-learn Training/config/stage3_full_team_resume.yaml --run-id=stage3_full_team --results-dir=Training/results --resume
```

2026-10-01 기준으로는 이 재개보다 아래 [수비 보조 보상](#3단계-수비-보조-보상-2026-10-01) 뒤 새로 시작하는 것을 권한다.

현재 학습 중이면 Ctrl+C 한 번 후 네 모델의 저장 완료를 기다리고, Unity Play를 정지한 다음 위 명령과 `Stage3_FullTeam` Play로 다시 연결한다. `--force`는 쓰지 않는다. TensorBoard에서 `BaseballFielder`의 `Policy`·`Losses`가 생겨 실제 업데이트가 시작됐는지 확인하고, 타자의 `Pitch Call/In Play`로 수비 기회가 얼마나 생기는지 함께 본다. `Environment/Cumulative Reward`나 스텝 증가만으로 학습 업데이트가 있었다고 판단하지 않는다.

### 3단계 수비 보조 보상 (2026-10-01)

위 설정으로 학습한 수비(약 494만 스텝)는 공을 쫓지 않았다. 매 플레이 정해진 방향으로 달려 파울 지역·홈 뒤쪽으로 나갔다. 인플레이의 약 99%가 12 s 시간 초과였고 아웃은 거의 없었다. 결과 보상만으로는 수비 행동에 신호가 가지 않았기 때문이다. 원인과 규칙은 [수비 보조 보상](../docs/fielding-agents.md#수비-보조-보상-2026-10-01)에 있다.

- **바뀐 것:** 수비 그룹에 첫 포구 +0.25·포스 베이스 커버 보조 보상이 생겼다. 후속 수정으로 쫓기는 가장 가까운 수비수 개인에게만 지급하고 계수를 0.02→0.05 /m로 올렸다. 같은 날 내야수(1루수·유격수·3루수)가 공을 처리하지 않을 때 자기 베이스(1·2·3루)를 덮도록 돕는 개인 보조 보상도 더했다([내야 역할 보조 보상](../docs/fielding-agents.md#내야-역할-보조-보상-2026-10-01)). 둘 다 같은 새 실행에서 시작하면 된다.
- **설정·이동:** 일반·재개·셀프플레이 YAML 모두 수비 `beta`는 1e-3이다. 수비 YAML의 `trainer_type: baseball_fielder_poca`가 선택하는 등록 확장에서 정책 출력은 기존 `/3` 스케일을 유지한 뒤 벡터 크기만 1로 제한한다. 같은 변환을 학습 행동과 ONNX 출력에 적용한다. Unity Agent도 성분별 클램프를 제거했다. 관측·행동 수·씬 참조는 그대로다.
- **실행 진입점:** 기존 `mlagents-learn` 명령을 그대로 쓴다. 저장소의 `Training/mlagents_extensions`를 Python 환경에 한 번 등록하면 CLI가 수비 전용 POCA 확장을 자동으로 불러온다. `auto_curriculum.py`도 모든 단계에서 일반 CLI를 쓴다. 코드 변경은 저장소에서 관리하고 타자·투수·주자 학습기는 그대로다. 이미 내보낸 옛 ONNX는 다시 내보내야 한다. `Training/train.py`는 이전 명령 호환용 별칭이다. 스크립트 추적 혼합은 후속 학습 결과를 보고 판단한다.
- **자리 유지:** 공을 쫓거나 쥐거나 송구 중인 수비수를 제외하고, 1루수·유격수·3루수는 담당 베이스의 3 m 반경 밖에서 작은 개인 감점을 받는다. 초당 `−min(0.05, 0.002 × 초과 거리)`이며 첫 결정 뒤 고정 단계 시간으로 누적한다. 반경 안은 0이고 종료 때 돌려주지 않는다. 이는 포텐셜 보조 신호와 별개의 실제 역할 목표다. 2루는 유격수가 맡는다.
- **변환 검증:** 같은 ML-Agents Python 환경에서 `python Training/verify_fielder_actions.py`로 환경 행동·원본 optimizer 행동·다른 Agent 회귀·ONNX 방향을 검사한다. 학습은 실행하지 않으며 테스트 ONNX만 임시 폴더에 만든다.
- **기존 결과:** 이전 수비 체크포인트는 읽히지만 떠돈 가중치다. 3단계를 새로 시작한다. 기존 결과는 이름을 바꿔 보존한다.

새 Python 환경에서는 저장소 루트에서 한 번 등록한다. 현재 `C:\miniconda3\envs\mlagents` 환경에는 등록했다. ML-Agents 1.1.0을 사용하는 환경에서 실행한다.

```powershell
python -m pip install --no-deps --no-build-isolation --no-index -e Training/mlagents_extensions
```

그다음 기존 명령으로 학습한다. 현재 학습이 실행 중이면 먼저 저장 후 종료한다.

```powershell
Rename-Item Training\results\stage3_full_team stage3_full_team_noshaping_20261001
mlagents-learn Training/config/stage3_full_team.yaml --run-id=stage3_full_team --results-dir=Training/results
```

- **실행:** 저장소 루트에서 실행한다. "Start training by pressing the Play button"이 나오면 Editor에서 스크립트 컴파일이 끝났는지 확인하고 `Stage3_FullTeam`을 Play한다.
- **출발점:** 타자·투수는 2단계 체크포인트에서, 수비·주자는 새 모델로 시작한다.
- **자동 실행:** `python Training/auto_curriculum.py --start-stage 3`은 `--skip-build` 없이 실행해 새 코드로 다시 빌드한다.
- **TensorBoard로 확인:**
  - `Defense/Fielded`(수비가 타구를 잡은 비율)가 먼저 올라야 한다. 이어서 `Play/Outs`·`Play End/Force Out`·`Fly Out`이 늘고 `Env/Fielding Timeout`이 줄어야 한다.
  - 내야는 `Defense/Cover 1B`가 가장 먼저 오를 것으로 본다(거의 모든 땅볼에 타자주자가 1루로 온다). `Cover 2B`·`Cover 3B`·`Cover Home`은 누상 주자가 있을 때만 기록돼 표본이 적다(2026-10-02부터 베이스 기준 이름).
  - `Defense Reward/Shaping`은 그룹 포구·포스 커버 합, `Defense Reward/Chase Shaping`은 개인 쫓기 보상 총합이다. 둘 다 할인 없는 합이라 수비가 못해도 커질 수 있으니 실력 판단에 쓰지 않는다. 개인 쫓기는 `Environment/Group Cumulative Reward`에는 포함되지 않는다.
  - `Defense Reward/Position`(2026-10-02 이전 `Infield Position`)은 개인 자리 이탈 감점의 합이다. 0에 가까울수록 해당 플레이에서 이탈 감점이 적었다. 플레이 시간·역할 면제에 영향을 받으므로 실제 커버율·포구율도 함께 본다.
  - 연결 중인 학습기의 `beta`는 YAML 저장만으로 바뀌지 않는다. 저장 후 재시작해야 하며, 독립 실행 파일은 새 코드로 다시 빌드해야 한다. 학습기 자동 재시작·결과 폴더 이동은 이 코드 수정에서 실행하지 않았다.
- **재개:** 이 실행을 멈췄다가 이어 갈 때는 위 `stage3_full_team_resume.yaml`과 `--resume`을 쓴다.

### 3단계 수비 9명·역할 임무 보상 (2026-10-02)

수비를 투수·포수·1B·2B·3B·SS·LF·CF·RF 9명으로 늘리고 보상을 개편했다. 학습한 수비가 공을 치면 자기 구역을 벗어나 떠도는 문제 때문이다. 규칙은 [수비 9명·역할 임무 보상](../docs/fielding-agents.md#수비-9명역할-임무-보상-개편-2026-10-02)에 있다.

- **바뀐 것:**
  - 처음 공을 잡은 수비수에게 개인 포구 보상 +0.3을 준다. 끝에서 회수하지 않는다.
  - 공을 처리하지 않는 수비수 모두에게 역할 임무가 있다. 포수 홈, 1·3루수 자기 베이스, 2루는 타구 방향에 따라 2루수 또는 유격수, 투수는 1루수가 공을 처리할 때 1루, 외야는 자기 구역 12 m다.
  - 임무 지점 밖 자리 감점은 초당 최대 0.1이다(이전 내야만 0.05).
- **호환:** 수비 관측 65→77, 주자 관측 57→65다. 기존 `stage3_full_team`의 수비·주자 체크포인트로는 재개할 수 없다(`stage3_full_team_resume.yaml` 포함). 3단계를 새 실행으로 시작한다. 타자·투수 관측은 그대로다.
- **씬:** 저장소의 `Stage3_FullTeam`은 새 구성(경기장 4개 × 9명)으로 다시 만들어 두었다. 다른 경기장 수가 필요하면 `Build Stage 3 Scene`으로 다시 만든다. 독립 실행 파일은 다시 빌드한다(`auto_curriculum.py`를 `--skip-build` 없이 실행).

현재 학습이 실행 중이면 Ctrl+C 한 번 후 저장이 끝나기를 기다린다. 그다음 기존 결과를 보존하고 새로 시작한다.

```powershell
Rename-Item Training\results\stage3_full_team stage3_full_team_5fielders_20261002
mlagents-learn Training/config/stage3_full_team.yaml --run-id=stage3_full_team --results-dir=Training/results
```

- **출발점:** `stage3_full_team.yaml`은 타자·투수를 2단계 체크포인트에서 시작한다. 직전 3단계에서 이어 학습한 타자·투수를 쓰려면 두 `init_path`를 보존한 폴더의 `BaseballBatter/checkpoint.pt`·`BaseballPitcher/checkpoint.pt`로 바꾼다.
- **TensorBoard로 확인:** `Defense/Fielded`와 `Defense Reward/Fielding`(= 포구율 × 0.3)이 먼저 올라야 한다. 이어서 `Defense Reward/Position`이 0 쪽으로 오르고(자리 이탈 감소), `Env/Fielding Timeout`이 줄고 `Play/Outs`가 늘어야 한다. `Defense/Cover Home`은 포수가 홈을 지키는지 본다.

### 3단계 수비만 다시 학습 (2026-10-02)

9명으로 처음 학습한 수비는 거의 서 있고, 공을 잡자마자 아무도 없는 베이스로 던지는 정책으로 굳었다. 원인과 수정은 [수비 정지·즉시 송구 수정](../docs/fielding-agents.md#수비-정지즉시-송구-수정-2026-10-02-후속)에 있다. 이동 배율 3, 송구 마스크(공을 쥔 수비수만 송구 선택), 수비 `beta` 5e-3을 적용했다.

수비만 새로 시작하고 타자·투수·주자는 그 실행에서 이어 받는다. 학습 중이면 Ctrl+C 한 번 후 저장이 끝나기를 기다린 뒤 실행한다. Unity Editor는 스크립트 컴파일이 끝난 상태여야 한다.

```powershell
Rename-Item Training\results\stage3_full_team stage3_full_team_9f_try1_20261002
mlagents-learn Training/config/stage3_full_team_refield.yaml --run-id=stage3_full_team --results-dir=Training/results
```

- `stage3_full_team_refield.yaml`은 위 이름의 보존 폴더에서 타자·투수·주자 `checkpoint.pt`를 `init_path`로 읽는다. 폴더 이름을 바꾸면 YAML의 세 경로도 같이 바꾼다. 스텝 수는 새 실행에서 0부터 다시 센다.
- **TensorBoard로 확인:** `BaseballFielder`의 `Policy/Entropy`가 이전처럼 0.2 근처로 급격히 떨어지지 않는지 본다. `Defense/Fielded`가 오르고, `Play End/Runner Safe`·`Play/Outs`가 0에서 올라오며 `Env/Fielding Timeout`이 내려가야 한다. 타자 쪽 `Plate Appearance/On Base`가 66~80%에서 내려오면 수비가 홈 앞 땅볼을 처리하기 시작한 것이다.

## 병렬 학습

단계 씬에는 경기장이 기본 4개 들어 있다. 400 m 간격으로 복제돼 있고, 모든 경기장이 한 Unity 안에서 동시에 데이터를 모은다.

- **개수 바꾸기:** `Tools > Baseball Simulation > Training > Arena Count > 1 / 4 / 8`을 고른 뒤 `Build Stage N Scene`으로 다시 만든다. 경기장이 많을수록 한 스텝에 모이는 데이터가 늘지만 Unity 프레임이 느려진다.
- **더 늘리기:** 씬을 독립 실행 파일로 빌드해 `--env=<빌드 경로> --num-envs=N --no-graphics`로 여러 프로세스를 띄운다. 이때 빌드 설정(Build Profiles)에 해당 씬을 넣고, Player 설정의 Run In Background를 켠다.
- **이전 모델과의 호환:** 2026-09-27 이전에 3단계(수비·주자)를 학습했다면 그 모델은 관측 좌표가 절대 좌표라 새 씬과 맞지 않는다. 다시 학습한다. 1·2단계 모델은 그대로 쓸 수 있다.
- **이전 실행 파일:** 병렬 경기장 이전에 만든 `Training/builds/` 실행 파일은 경기장 1개에 이전 관측 좌표를 쓴다. 자동 실행을 `--skip-build` 없이 한 번 돌려 다시 빌드한다.

## 확인할 것

- Play 중 Console에 `[TrainingEnvController] ... 플레이를 중단한다` 경고가 반복되면 투구 거부나 멈춘 플레이가 있다는 뜻이다.
- 학습기 없이 Play하면 모든 Agent가 중립 행동을 한다.
  - 타자: 스윙하지 않는다. 단 2·3단계의 고정 타자 평가 타석(약 10%)에서는 `Models/BenchmarkBatter_Stage1.onnx`가 친다.
  - 투수: 포심을 존 중앙에 던진다. 기준 투수 평가 타석(약 10%)에서는 스크립트가 여러 구종을 던진다.
  - 수비: 제자리에 선다.
  - 주자: 판단하지 않는다.
- 동작 점검은 Play → 일시정지 → `Tools > Baseball Simulation > Training > Verify Open Training Scene`으로 한다. 2·3단계 평가 타석은 같은 메뉴의 `Verify Benchmark Plate Appearances`로 확인한다.
