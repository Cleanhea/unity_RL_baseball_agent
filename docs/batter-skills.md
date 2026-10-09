# 타이밍부터 분리하는 타자 학습과 고정 평가

## 타이밍 집중·정타 진행 보상 강화 (2026-10-09)

Camera30 타자는 배트 위치·각도를 고정한 타이밍 과정부터 높이·각도·배트 위치를 차례로 열고 전체 준비로 연결한다. 몸 고정·30장·100Hz와 최종 정타·성적 통과 기준은 유지한다. 준비 과정 및 `batter_prepared=1`의 정타 미달 실제 페어 보상 상한을 0.5→1.5로 높이고 접촉 품질 75%·속도 25%로 진행 보상을 강화한다. [최신 계약·수식·실행](batter-timing-progress.md)을 따른다. 아래 약한 페어 0~0.5 기록은 변경 전 설명이며 준비 모드가 아닌 기존 학습에는 계속 적용한다.

## 타자 몸 위치 고정 (2026-10-09)

학습 타자의 몸 위치는 모든 과정에서 기준 위치로 고정한다. 기존 모델을 이어 쓰도록 행동 크기를 유지하며 몸 이동 행동 0·1만 무시한다. 배트 위치·스윙 각도·타이밍·30장 입력·성적 기준은 유지한다. [계약·학습 재개·검증](batter-fixed-stance.md)을 따른다. 아래 몸 이동 관련 기록은 변경 전 설명이다.

## 영상 30장·100Hz 적용 (2026-10-09)

현재 타자 영상 스택은 6→30장으로 변경했다. 256×256 흑백과 10ms 촬영/판단을 유지하며, 투구 중 연속 관측의 시간 범위는 290ms다. 기존 6장 모델의 1,062,459스텝 가중치를 별도 30장 초기화 모델로 옮기고, 전체 준비 과정 1에서 새 학습을 시작했다. Camera30 설정은 타자 배치 32·버퍼 256·궤적 64를 사용한다. 기존 6장 설정·실행 파일·학습 결과는 보존했다. [입력·모델 이식·메모리·실행 안내](batter-camera-30.md)를 따른다. 아래 6장 기록은 이전 검증 결과다.

2026-10-09 사용자 요청으로 기존 모델의 고정 평가와 선택적 `batter_skill` 준비 과정을 추가했다. 기존 학습은771,410스텝에서 정상 저장했다. 평가와 새 학습은 별도 체크포인트 복사본을 사용한다.

같은 날 후속 요청으로 현재 학습을 유지한2→3단계 대기 프로세스를 연결했다. 분리10개→전체 준비16개→2단계 기존 흐름을 유지하고,2단계 실제 완료 모델을 확인하면6장3단계로 이어간다. [연결 조건·상태·실행](stage23-continuation.md)을 따른다.

## 관측·행동과 책임

- 포수 시점256×256 흑백6장,10ms 촬영·판단, 자기 상태 벡터16을 유지한다. 공의 정답 위치·속도는 관측에 넣지 않는다.
- 행동 크기는 연속7+이산[2] 그대로다. 환경은 아직 열리지 않은 연속 제어를 무시하고 타자는 스윙 시작 시점을 판단한다.
- `TrainingEnvController`는1단계의 **새 타석**에서 `batter_preparation=0`과 `batter_skill`을 읽는다. 기본값−1과2·3단계는 기존 동작이다.
- `BatterSkills`는 제어별 범위와 직구 위치 분포를 정하며 `BatterAgent`가 기존 명령 경계에 적용한다. 공·배트·충돌 물리, 정타 정의, 보상은 유지한다. 접촉 보너스를 추가하지 않는다.

## 분리 준비 과정

모든 과정은 포심120~150km/h와0.30초 이전 스윙 제한을 사용한다. 아래 제어 비율은 기존 준비0의 절반 범위에 대한 비율이다.

| 과정 | 이름 | 배트 높이 | 스윙 각도 | 타자 X/Z·배트 X/Z | 투구 위치 표준편차 X/Y(m) |
| --- | --- | ---: | ---: | ---: | --- |
| 0 | Timing | 0 | 0 | 0 | 0 / 0 |
| 1 | Height_25 | 25% | 0 | 0 | 0 / 0.02 |
| 2 | Height_50 | 50% | 0 | 0 | 0 / 0.04 |
| 3 | Height_100 | 100% | 0 | 0 | 0 / 0.08 |
| 4 | Angles_25 | 100% | 25% | 0 | 0 / 0.08 |
| 5 | Angles_50 | 100% | 50% | 0 | 0 / 0.08 |
| 6 | Angles_100 | 100% | 100% | 0 | 0 / 0.08 |
| 7 | Position_25 | 100% | 100% | 25% | 0.0175 / 0.08 |
| 8 | Position_50 | 100% | 100% | 50% | 0.035 / 0.08 |
| 9 | Position_100 | 100% | 100% | 100% | 0.07 / 0.08 |

Timing은 기존 중앙 정타 과정1에서 물리 검증한 기준 자세·기준 각도를 사용한다. 중립 배트 Y는 `clamp(존 중앙 - PitchTarget 높이)`(기본−0.245m)다. 높이 제어를 여는 동안 기존 준비의 중립점−0.1225m로 점진적으로 이동한다. 과정9는 기존 준비0과 행동 의미·투구 분포·난수 사용 순서가 같다.

페어율≥65%와 정타율≥8%를 **2만 스텝 구간에서3회 연속** 만족해야 다음 과정으로 간다. 구간마다 완료 타석50개 이상, 과정별 최소6만 스텝이 필요하다. 실패·혼합 과정·비유한 값은 연속 통과를 초기화한다. 정타는 홈런 또는 품질≥0.6·속도≥120km/h·발사각5~35°인 페어 타구다.

`Batter Skills/Phase` Histogram과 기존 완료 타석의 페어·정타 Histogram을 읽고 `skills-status.json`에 저장한다. `batter_preparation`은0으로 유지되므로 기존 Phase 지표를 대신 읽어 전환하지 않는다.

과정9 통과 후 모델을 정상 저장한다. `--continue-stage2`가 있으면 별도 `<run-id>_prepare` 실행에서 기존16개 준비 과정으로 이어간다. 스윙 제한 해제·전체 제어·위치 확대·5구종까지 실제 성적을 통과한 뒤에만2단계로 간다. 분리 준비의 `ready=true`는 전체 준비 완료가 아니다. 직접 초기화 경로는 새 실행의 `--initial-checkpoint=경로`만 허용하며 재개와 함께 쓰지 않는다.

## 실행·저장·재개

```powershell
cd C:\MainScreen\Dev\GitDirectory\unity_RL_baseball_agent
conda activate mlagents
python -u -B Training/prepare_batter.py Training/config/batter_skills_camera6.yaml --skills --run-id=stage1_batter_camera6_skills --results-dir=Training/results --env=Training/builds/BatterSkillsCamera6/BatterSkillsCamera6.exe --base-port=57362 --continue-stage2 --env-args -batchmode
```

기존 run-id를 덮어쓰지 않는다. 재개는 같은 명령에 `--resume`을 더한다. 상태 이력을 실제 저장 스텝에 맞추고 연속 통과를 다시 확인하며 다른 준비 계획의 상태는 거부한다. **현재 실행 중인 결과 폴더**에 빈 `stop-requested` 파일을 만들면 정상 저장 후 중단하고 다음 실행으로 연결하지 않는다. 재개 전에 이 파일을 지운다.

## 고정 정책 평가

`Training/evaluate_batter.py`는 실제 Unity Player에서 모델을 고정하고 완료 타석을 모은다. 학습기·optimizer를 만들지 않으며 체크포인트 SHA256을 전후에 확인한다. 별도 포트·로그·JSON을 사용하고 기존 결과를 덮어쓰지 않는다.

- `deterministic`: 연속 행동 평균과 마스크를 적용한 이산 argmax.
- `stochastic`: 학습 때처럼 행동을 샘플링하며 가중치는 고정.
- `pose_mean`: 연속 행동 평균과 샘플링한 이산 스윙. 자세 잡음과 스윙 선택을 구분하는 진단용이다.
- `both`: deterministic·stochastic 각각 지정한 타석 수를 평가한다.

```powershell
python -u -B Training/evaluate_batter.py --checkpoint=Training/evaluations/batter_camera6_20261009_771410/checkpoint.pt --output=Training/evaluations/batter_camera6_20261009_771410/evaluation.json --mode=both --plate-appearances=500 --base-port=57361
python -u -B Training/evaluate_batter.py --checkpoint=Training/evaluations/batter_camera6_20261009_771410/checkpoint.pt --output=Training/evaluations/batter_camera6_20261009_771410/pose_mean.json --mode=pose_mean --plate-appearances=500 --base-port=57364
```

페어·정타율은 완료 타석, 컨택률은 실제 스윙, 속도·품질·각도는 접촉 타구, 비거리는 페어 타구의 첫 지면/펜스 접촉 기준이다. seed는 Unity·정책 초기화에 사용하고 스크립트 투수 시드는 씬의 값을 따른다. 정책별 타석당 투구 수가 달라 타석별 투구가 일대일 대응하지 않는다. argmax에서 스윙이 없어지는 결과는 샘플링 정책 성능과 함께 해석한다.

실제 평가 수치와 실행 검증은 [검증 기록](verification.md)에 기록한다. 새 학습의 장시간 성능은 실제 로그로 확인해야 한다.
