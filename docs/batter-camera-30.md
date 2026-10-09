# 타자 영상 30장·100Hz

## 타자 카메라 192×192·12장 (2026-10-09)

사용자 요청으로 현재 학습 씬의 타자 카메라를 **192×192 흑백·12장·100Hz**로 변경한다. 최근 영상 범위는110ms이며 기존256×256·30장 대비 영상 원소 수가77.5% 줄어든다. 기존30장 학습은 정상 저장했고, 새 입력에 맞춘 타자 가중치와 기존 투수·주자·CF 가중치를 별도 초기값으로 보존한다. 타자 이식은 근사 초기화이므로 기존 타격 성능을 그대로 보장하지 않는다. 새 run-id와 새 Player를 사용한다. 현재 계약·실행·검증은 [타자 Camera12_192](batter-camera12-192.md)를 따른다. 아래30장·6장 기록은 이전 설정이다.

## 영상 프레임 재사용·수동 3단계 직행 (2026-10-09)

사용자 요청으로 30장·해상도·픽셀 값을 유지한 PNG 프레임 캐시와 흑백 배열 변환 최적화를 적용한다. 기존 타자 모델을 별도 보존해 준비·2단계를 생략하고 수동 명령으로3단계에 직접 사용할 수 있다. 투수·주자·CF는 새로 학습하며 기존 성적 통과로 간주하지 않는다. 기존 자동 연결은 취소했다. [계약·검증·첫 실행/재개 명령](batter-visual-cache-stage3.md)을 따른다.

2026-10-09 후속 요청으로 타자 몸 위치를 모든 과정에서 기준 위치에 고정했다. 행동 크기를 유지하고 몸 이동 행동 0·1만 무시하며 배트 위치·스윙 제어는 유지한다. 현재 학습은 92,652스텝에서 정상 저장한 뒤 고정된 새 Player로 재개했다. [위치 고정 계약·검증](batter-fixed-stance.md)을 따른다.

2026-10-09 사용자 요청으로 타자 영상 스택을 6→30장으로 늘린다. 해상도256×256·흑백·촬영/판단10ms는 유지한다. 한 번의 영상 입력은 `[30,256,256]`이고 가장 오래된 영상부터 최신 영상까지290ms다. 매 판단에서 새로 촬영하는 영상은1장이며, 스윙 시작 후에는 기존처럼 추가 타자 결정을 요청하지 않는다. 자기 상태 벡터16·연속 행동7·이산[2]·물리·보상·구종·수비 규칙은 유지한다.

## 씬과 모델

`BatterAgent.ImageStacks=30`과 세 학습 씬의 모든 `CatcherEye`를 함께 맞춘다. 카메라 위치·FOV·GUID를 보존하며 복제 Unity 프로젝트의 Editor API로 씬을 갱신한다. 고정 평가 타자 경로는 `Models/BenchmarkBatter_Stage1_Camera30.onnx`다. 해당 모델이 없으면 고정 타자 평가는 비활성이다.

6장 모델을30장 센서에 직접 연결하거나 기존 run-id로 재개하지 않는다. 기존6장 학습은 전체 준비 과정1에서1,062,459스텝에 정상 체크포인트/ONNX를 저장해 종료했다. 기존3단계 대기도 취소했다. 원래6장 결과·설정·실행 파일은 보존한다.

`expand_camera_checkpoint.py --target-stacks=30`은 별도 초기화 파일에 actor·critic의 첫 CNN 가중치를 옮긴다. 기존6개 채널의50/40/30/20/10/0ms 영상은 새 채널24..29에 대응한다. 나머지24개 채널은0으로 시작하며, 다른 가중치는 그대로이고 Adam 모멘트는 초기화한다. 동일한 최신6장에 대한 초기 특징·가치 출력을 보존하는 이식이며, 추가24장은 역전파로 학습한다. 가중치 보존은 정타율 개선을 의미하지 않는다.

## 메모리와 실행

현재 장비는 RAM 약32GB, RTX5060Ti16GB다. 영상 입력은5배로 늘므로 새 Camera30 설정의 타자 `batch_size=32`, `buffer_size=256`, `time_horizon=64`로 조절한다. float32 영상만의 배치/버퍼 크기는240MiB/1.875GiB다. 이는 전체 RAM/VRAM 사용량이 아니며 궤적·복호화·학습 복사본도 필요하다.

실제 짧은 학습에서버퍼512·궤적128은 학습 프로세스 묶음의 working set이8.43GiB까지 늘고 시스템 RAM 여유가1.59GiB까지 줄었다. 버퍼256·궤적64로 재검증한 피크는6.56GiB, 시스템 RAM 여유는 최소3.63GiB였다. 이 변경은 PPO의 갱신 빈도·부트스트랩 경계도 바꾸며 영상30장과10ms 간격에는 영향이 없다. 요약·최소 과정 스텝·할인율/GAE·학습률·탐색 계수·학습 한도는 기존100Hz 설정을 유지한다. 메모리 표본은2초 간격이며 더 긴 실행의 최대값을 보장하지 않는다.

```powershell
conda activate mlagents
python -u -B Training/expand_camera_checkpoint.py --source=Training/results/stage1_batter_camera6_skills_prepare/BaseballBatter/checkpoint.pt --output=Training/initialization/batter_camera30_100hz/checkpoint.pt --target-stacks=30
python -u -B Training/prepare_batter.py Training/config/batter_preparation_camera30.yaml --camera-stacks=30 --initial-phase=1 --run-id=stage1_batter_camera30_prepare --results-dir=Training/results --env=Training/builds/BatterCamera30/BatterCamera30.exe --base-port=57370 --continue-stage2 --env-args -batchmode -force-d3d11
```

초기화 파일이 이미 있으면 변환 명령을 반복하지 않는다. 새 실행은 기존 준비 과정1에서 시작하며 새 스텝0·새 성적 구간으로 진행한다. 재개할 때는 `--initial-phase=1`을 빼고 `--resume`을 더한다. 현재 결과 폴더에 `stop-requested` 파일을 만들면 정상 저장하고 후속 단계는 시작하지 않는다. 재개 전 이 파일을 제거한다.

전체 준비 과정15의 성적·저장 모델을 확인하면 `stage2_batter_pitcher_camera30_selfplay.yaml`과 `Stage2_Camera30.exe`로 연결한다. `continue_stage3.py --camera-stacks=30`은 이 준비 체인의 종료·2단계 실제 타자/투수 저장 스텝·30장 입력·유한 가중치·최종 모델을 확인하고 `stage3_full_team_camera30_selfplay.yaml` 및 `Stage3_Camera30.exe`로 연결한다. 주자·CF는 새로 학습하며2·3단계 결과 경로도6장 실행과 다르다.

## 검증

`Training/verify_camera30.py`는 실제6장 모델의 actor·critic 엄격 로드, 관련 없는 가중치 불변, 동일 최신6장의 출력 일치, 새24개 채널의 학습, 새 Adam 로드,30장 ONNX checker와 새 CLI의 준비 과정1 적용을 확인한다. 기존 준비·분리 준비·2→3단계 검증은6장 경로의 회귀도 확인한다.

실제 Unity의 세 단계 센서·관측·타격·초기화와 세 Player 빌드는 통과했다. Python의 실제 학습1,074스텝에서30장·10ms·290ms·준비 과정1, actor·critic 추가 채널의 갱신·유한 가중치·정상 체크포인트/ONNX 저장을 확인했다. 시작 비용 포함 처리량은 초당 약11.75개 결정이다. 네 경기장의 합산이며 시뮬레이션100Hz와 다르다. 완료4타석은 모두 삼진으로, 짧은 실행은 타격 성능 평가가 아니다.

본 학습은 별도 `stage1_batter_camera30_prepare`로 시작했고 `stage23_camera30_chain`에 후속 단계 대기를 연결했다. 후속 실행 점검에서 11,128스텝을 정상 저장하고 같은 스텝·준비 과정1로 재개했다. 현재 실행 PID와 로그 경로는 `Training/evaluations/camera30_20261009/session.json`에 있다.

11,128스텝 모델의 복사본으로 별도 Player에서 준비 과정1·확률적 행동·50타석을 평가했다. 실제 입력30장·10ms·290ms를 확인했으며 페어2/50(4%)·정타1/50(2%)·삼진48/50(96%)였다. 존 공의 스윙은7/152(4.61%)였다. 영상 입력과 학습은 동작하지만 현재 타격 성능은 낮다. 단일 난수 시드의 초기 모델 평가이며 공 전용 렌더도 함께 변경되어, 스택 증가만의 효과나 장기 개선을 판단하지 않는다. 결과는 `Training/evaluations/camera30_20261009/health/batting-11128.json`이다.

본 학습 첫11,128스텝의 프로파일은840.70초·13.24결정/초였다. worker 시간827.12초 중 영상 관측 변환에514.18초(62.16%)를 썼다. 서로 병렬인 parent와 worker 시간을 합산하지 않는다. 평가를 함께 실행한 추가 모니터링에서는 학습 프로세스 합산 working set 피크7.73GiB·평가 프로세스 피크1.86GiB·시스템 RAM 최소 여유3.17GiB였다. 이는3초 간격 표본이며 공유 페이지 중복 가능성과 다른 앱의 메모리 사용을 포함한 제한이 있다. 준비 완료 및 실제2→3단계 전환은 아직 확인하지 않았다. 최신 결과는 [검증 기록](verification.md)을 따른다.
