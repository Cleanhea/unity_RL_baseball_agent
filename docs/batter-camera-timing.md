# 타자 영상 스택과 촬영·판단 빈도 개선

## 영상 30장·100Hz 적용 (2026-10-09)

현재 타자 영상 스택은 6→30장으로 변경했다. 256×256 흑백과 10ms 촬영/판단을 유지하며, 투구 중 연속 관측의 시간 범위는 290ms다. 기존 6장 모델의 1,062,459스텝 가중치를 별도 30장 초기화 모델로 옮기고, 전체 준비 과정 1에서 새 학습을 시작했다. Camera30 설정은 타자 배치 32·버퍼 256·궤적 64를 사용한다. 기존 6장 설정·실행 파일·학습 결과는 보존했다. [입력·모델 이식·메모리·실행 안내](batter-camera-30.md)를 따른다. 아래 6장 기록은 이전 검증 결과다.

2026-10-08 사용자 요청으로 영상 스택과 촬영·판단 빈도를 함께 늘렸다. 해상도와 색상은256×256 흑백으로 유지한다.

| 항목 | 이전 | 현재 |
| --- | --- | --- |
| 영상 관측 | `[3,256,256]` | `[6,256,256]` |
| 고정 물리·타자 결정 간격 |20ms|10ms|
| 시뮬레이션 시간 기준 촬영·판단 빈도 |50Hz|100Hz|
| 최신 영상부터 가장 오래된 영상까지 |40ms|50ms|

스택6과100Hz를 함께 적용하므로 과거100ms가 아니라50ms를 더 촘촘히 관찰한다. Unity 카메라는 타자 결정마다 렌더하고 ML-Agents `StackingSensor`는 오래된 영상부터 최신 영상 순서로 쌓는다. 실제 벽시계 처리 속도는 GPU·렌더링·학습 비용에 따라 달라진다. 공의 정답 위치·속도를 추가하지 않는다.

## 환경과 씬

`BatterAgent.ImageStacks=6`, `DecisionIntervalSeconds=0.01`이며 프로젝트의 `TimeManager.asset`도10ms다. 기존 매 고정 단계의 결정 요청 흐름을 사용한다. 투구·배트·충돌·수비 물리 적분도10ms로 바뀌며 투구 해석기는 같은 간격을 사용한다. 공·배트 크기, 속도 범위, 접촉 품질 기준과 보상은 유지한다.

`TrainingEnvController`에 저장된 수비·주자 결정 간격과 최대 플레이 단계 수는50Hz 기준으로 환산해 **기존 초 단위 지속 시간**을 유지한다. 기본 수비·주자 결정은10고정 단계=100ms, 컨트롤러 제한은3,000단계=30초다. 타자는 투구 중10ms마다 새 결정을 내린다. 타석 전 자세 결정과 투수의 투구 결정은 기존처럼 각1번이다.

`TrainingSceneBuilder.UpdateBatterCameraSensors`는 기존 센서의 해상도·흑백·스택 계약을 맞추고 카메라 위치/FOV를 보존한다. 계약이 바뀐 타자의 이전 추론 Model 참조를 해제한다. 고정 타자 평가의 새 경로는 `Models/BenchmarkBatter_Stage1_Camera6.onnx`다. 모델이 없으면 해당 평가를 사용하지 않는다. 이전3장 모델을 새 센서에 직접 연결하지 않는다. 씬은 복제 Unity 프로젝트에서 Editor API로 갱신하고 참조 검증 후 원본에 적용한다. 씬과 `.meta` GUID를 유지한다.

TensorBoard의 `Batter Observation/Stack Count`, `Decision Interval (ms)`, `History Span (ms)`에 실제 실행의 계약과 시간 간격을 기록한다.

## 기존 가중치 보존

3장 체크포인트는6장 모델과 직접 호환되지 않는다. `Training/expand_camera_checkpoint.py`는 별도 초기화 파일에 actor·critic의 첫 CNN 가중치를 이식한다. 새 채널1·3·5는 각각40·20·0ms 전 영상이며 기존3장과 같은 시점이다. 이 채널에 기존 가중치를 넣고50·30·10ms 채널은0으로 시작한다. 나머지 가중치는 모두 유지한다. Adam 모멘트는 새 초기화에서 다시 시작한다.

이는 **동일한 영상에서 기존 네트워크 출력을 보존하는 이식**이다. 물리 간격과 입력 분포가 바뀐 뒤의 실제 타격 성능까지 보장하지 않는다. 새 채널은 역전파로 학습할 수 있다. 원본 체크포인트를 수정하지 않으며 기존 출력 경로가 있으면 변환을 거부한다. 출처·SHA256·원본 스텝·채널 대응은 초기화 파일 옆 JSON에 남긴다.

이번 새 실행은 이전 제한 해제에서 정타가 저하된349,991스텝 준비 체크포인트 대신, 원래1단계928,667스텝 모델을 이식해 준비 과정0부터 시작한다. 기존 학습은349,991스텝의 체크포인트와 같은 스텝 ONNX가 저장된 것을 확인하고 종료했다. Windows 콘솔 중단 요청이 반영되지 않아 이 저장분을 보존한 뒤 작업 소유 프로세스만 종료했다. 원래 결과를 덮어쓰지 않는다.

## 학습과 자동 연결

새 설정은 `Training/config/batter_preparation_camera6.yaml`이다. 실제 페어 타구·정타·선구 기준은 [준비 과정](batter-preparation.md)과 같다. 판단 빈도가 두 배이므로 요약20,000·최소 과정60,000·체크포인트100,000·최대4,000,000스텝으로 기존 대략적인 투구 경험량을 유지한다. 영상 버퍼는2,048, 배치는128로 유지해 입력 크기 증가를2배로 제한한다.

타자의 할인율과 GAE 계수는 기존값의 제곱근(`gamma≈0.994987`, `lambda≈0.974679`)으로 환산해 같은 시뮬레이션 시간 동안 보상·추정 오차의 감쇠를 유지한다. 투수는1투구1결정이므로 기존값을 유지한다. 이는 관측 빈도 증가로 보상 전달 시간이 절반으로 줄어드는 부작용을 보정하는 설정이다.

```powershell
cd C:\MainScreen\Dev\GitDirectory\unity_RL_baseball_agent
conda activate mlagents
python -u -B Training/expand_camera_checkpoint.py --source=Training/results/stage1_batter_camera_power/BaseballBatter/checkpoint.pt --output=Training/initialization/batter_camera6_100hz/checkpoint.pt
python -u -B Training/prepare_batter.py Training/config/batter_preparation_camera6.yaml --run-id=stage1_batter_camera6_prepare --results-dir=Training/results --env=Training/builds/BatterCamera6/BatterCamera6.exe --base-port=57351 --continue-stage2 --env-args -batchmode
```

초기화 파일은 이미 생성했다면 변환 명령을 다시 실행하지 않는다. 학습 재개는 **두 번째 명령에 `--resume`**을 추가한다. 실행 중에는 같은 학습을 중복 시작하지 않는다. 준비 스크립트는3장 초기화/재개 체크포인트를 거부한다. 기존3장 run-id로6장 학습을 재개하지 않는다.

준비 학습을 저장하고 멈추려면 `Training/results/stage1_batter_camera6_prepare/stop-requested`라는 빈 파일을 만든다. 파라미터 관리자에서 중단을 받아 ML-Agents의 정상 체크포인트/ONNX 저장 경로로 종료하고2단계 자동 연결을 실행하지 않는다. 재개할 때는 이 요청 파일을 지운 뒤 같은 명령에 `--resume`을 더한다. Windows 콘솔 Ctrl+C가 전달되지 않는 경우를 위한 준비 학습 전용 경계다.

준비 과정15의 성적 통과와 체크포인트 저장을 확인하면 `stage2_batter_pitcher_camera6_selfplay.yaml`·`Stage2_Camera6.exe`·별도 run-id `stage2_batter_pitcher_camera6_selfplay`·포트57352로 자동 연결한다. 타자의 결정 기반 최대 스텝·팀 교체·스냅샷·교체·요약 간격을 두 배로 환산하고, 투수의1투구1결정 간격은 유지한다. 세 단계의 센서 계약은 동일하다. 이전3장 YAML·결과·실행 파일은 과거 계약의 산출물이다.

## 검증

- `Training/verify_camera_expansion.py`: 실제1단계 actor·critic의 엄격한 가중치 로드, 동일 시점 영상의 특징/가치 출력 일치, 관련 없는 가중치 불변, 새 채널의 역전파·가중치 갱신,6장 ONNX 내보내기/checker.
- `Training/verify_preparation.py`: 실제 새 YAML, 최소 과정60,000스텝,3장 체크포인트 진입 거부, 기존 성적 조건·재개·정상 저장·셀프플레이 연결 검사.
- `Training/verify_batter_curriculum.py`:6장 CNN 순전파/역전파·행동 마스크·ONNX 및 기존 보상 커리큘럼 검사.
- Unity: 실제6장 카메라·10ms 간격·투구/타격·정타 보상·초기화·구종 조준·수비/주루·병렬 경기장을 검사한다. 빌드한 새 실행 파일과 Python의 실제 연결 및 첫 학습 요약도 확인한다.

장시간 정타율 향상은 별도 실제 로그로 평가한다. 초기 가중치 이식 검사를 학습 성능 개선으로 표현하지 않는다.

2026-10-08에는 위 Python/C#·Unity 세 단계 검사와 세 실행 파일 빌드를 통과했다. 새 학습의 첫20,000스텝에서6장·10ms·50ms 계약이 실제 통계에 기록됐고, 페어 타구81.91%·정타4.44%로 준비 과정0을 유지했다.31,180스텝에서 요청 파일을 통한 정상 체크포인트/ONNX 저장과 같은 run-id의 재개도 실제 검증했다. 추가한 영상 채널의 가중치가 학습된 것을 확인했으며 학습은 계속 진행 중이다. [상세 검증 기록](verification.md#1235-영상6장촬영판단100hz와-기존-가중치-이식-2026-10-08)을 참고한다.
