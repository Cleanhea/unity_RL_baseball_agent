# 학습 실행 안내

세 단계 커리큘럼의 설계와 씬 구성은 [docs/training-curriculum.md](../docs/training-curriculum.md)에 있다. 이 폴더에는 ML-Agents 학습 설정 파일이 있다. Unity가 가져오지 않도록 `Assets` 밖에 두었다.

| 단계 | 씬 | 설정 | run-id |
| --- | --- | --- | --- |
| 1 타자 | `Scenes/Training/Stage1_Batter` | `config/stage1_batter.yaml` | `stage1_batter` |
| 2 타자+투수 | `Scenes/Training/Stage2_BatterPitcher` | `config/stage2_batter_pitcher.yaml` | `stage2_batter_pitcher` |
| 3 전체 | `Scenes/Training/Stage3_FullTeam` | `config/stage3_full_team.yaml` | `stage3_full_team` |
| 2 셀프플레이 | 2단계와 같음 | `config/stage2_batter_pitcher_selfplay.yaml` | `stage2_batter_pitcher_selfplay` |
| 3 셀프플레이 | 3단계와 같음 | `config/stage3_full_team_selfplay.yaml` | `stage3_full_team_selfplay` |

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

스크립트가 원본 Editor와 분리된 임시 프로젝트에서 단계별 Windows 실행 파일을 `Training/builds/`에 만든다. 1 → 2 → 3단계 학습기를 차례로 실행하고, 각 단계의 **모든 Behavior가 YAML의 `max_steps`에 도달**해 최종 모델·체크포인트가 저장됐는지 확인한 뒤 다음 단계로 넘어간다. 보상 점수에 따른 전환은 아니다. 기존 결과 디렉터리를 덮어쓰지 않으며 빌드·학습 실패 시 다음 단계로 넘어가지 않는다. 각 단계는 자기 씬이 들어 있는 실행 파일을 사용하므로 Editor에서 씬을 바꿀 필요가 없다.

빌드는 격리 프로젝트의 에셋 가져오기와 스크립트 컴파일을 먼저 마친 뒤 별도 Unity 실행에서 진행한다. 단계 씬에 누락된 스크립트 참조가 있으면 실행 파일을 만들기 전에 중단한다. 가져오기 로그는 `Training/results/curriculum-import.log`에 남는다.

격리 프로젝트는 시스템 임시 폴더의 **긴 이름 경로**(예: `C:\Users\mr hong\AppData\Local\Temp\baseball-curriculum-build`)에 만든다. 이 PC의 TEMP는 8.3 짧은 이름(`C:\Users\MRHONG~1\...`)으로 잡혀 있다. 그 경로로 Unity를 열면 프로젝트 스크립트가 하나도 연결되지 않은 실행 파일이 만들어진다. 그러면 Agent가 없어 `mlagents-learn`이 `UnityTimeOutException`으로 끝난다. 반대로 경로가 너무 길면 Burst 빌드가 실패한다.

이미 Editor에서 1단계를 학습 중이면, 1단계 `mlagents-learn`의 PID를 확인한 다음 다음 명령을 별도 터미널에서 실행한다. 현재 학습이 끝날 때까지 기다렸다가 2·3단계를 실행한다.

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
mlagents-learn Training/config/stage1_batter.yaml --run-id=stage1_batter --results-dir=Training/results
```

"Start training by pressing the Play button"이 나오면 Unity Editor에서 해당 단계 씬을 열고 Play한다. 자동 실행과 별도로 수동 학습할 때 쓴다. 2·3단계 설정은 이전 단계 결과(`Training/results/<run-id>/<Behavior>/checkpoint.pt`)를 `init_path`로 읽는다. 그래서 1 → 2 → 3 순서로, 위 run-id 그대로 학습한다. run-id를 바꾸면 설정 파일의 `init_path`도 고친다.

- 진행 그래프: `tensorboard --logdir Training/results`
  - 보상·손실 외에 야구 지표도 나온다: 삼진·볼넷률, 스윙·컨택률, 타구 속도·발사각, 구종 비율, 3단계 플레이 결과 등.
  - 목록과 뜻은 [TensorBoard 지표](../docs/training-curriculum.md#tensorboard-지표)에 있다.
  - 2단계부터는 한쪽을 고정한 평가 타석의 성적이 `Benchmark Batter/*`·`Benchmark Pitcher/*`로 따로 나온다. 상대가 함께 바뀌는 보상 곡선과 달리 실력 변화를 같은 잣대로 본다. [고정 상대 평가](../docs/training-curriculum.md#고정-상대-평가-2026-09-27) 참고.
  - 같은 값이 모든 Behavior 로그에 들어가므로 `BaseballBatter` 쪽을 보면 된다.
  - 이 지표가 들어가기 전에 만든 `Training/builds/` 실행 파일에는 지표가 없다. `--skip-build` 없이 다시 빌드한다.
- 학습된 모델 사용: `Training/results/<run-id>/<Behavior>.onnx`를 해당 Agent의 Behavior Parameters `Model`에 넣는다.

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
