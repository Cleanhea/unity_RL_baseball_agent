# 영상 프레임 재사용과 수동 3단계 직행 (2026-10-09)

## 타자 카메라 192×192·12장 (2026-10-09)

사용자 요청으로 현재 학습 씬의 타자 카메라를 **192×192 흑백·12장·100Hz**로 변경한다. 최근 영상 범위는110ms이며 기존256×256·30장 대비 영상 원소 수가77.5% 줄어든다. 기존30장 학습은 정상 저장했고, 새 입력에 맞춘 타자 가중치와 기존 투수·주자·CF 가중치를 별도 초기값으로 보존한다. 타자 이식은 근사 초기화이므로 기존 타격 성능을 그대로 보장하지 않는다. 새 run-id와 새 Player를 사용한다. 현재 계약·실행·검증은 [타자 Camera12_192](batter-camera12-192.md)를 따른다. 아래30장·6장 기록은 이전 설정이다.

사용자 요청으로 영상 변환 최적화 1·2번과 1단계 모델의 3단계 직접 사용을 준비했다. 후속 요청에 따라 장기 3단계 학습은 사용자가 직접 명령으로 시작한다. 준비 완료/2단계 완료를 가정하거나 상태를 통과로 위조하지 않는다.

## 입력을 유지하는 최적화

`Training/visual_cache.py`와 전용 `Training/train_cached.py`를 사용한다. 설치된 ML-Agents 파일·Unity 씬·센서·물리·모델 구조는 수정하지 않는다. spawn된 환경 worker의 factory에서 영상 변환을 명시적으로 설치한다. 기존 `train.py`, `prepare_batter.py`, 일반 `mlagents-learn`은 자동으로 변경하지 않는다.

전체 PNG 바이트를 키로 삼아 이미 변환한 흑백 프레임을 재사용한다. 같은 바이트의 프레임만 공유하므로 서로 다른 Agent·초기화·프레임 순서가 섞이지 않는다. PNG 청크 길이와 IEND로 프레임을 나누며 재압축하지 않는다. RGB 정규화와 3채널 평균은 기존 float32 계산을 따른다. 캐시는 프레임+키 데이터 합계 최대64MiB의 LRU이고 읽기 전용 프레임을 보관한다. 반환 배열은 별도 메모리를 소유한다.

과거29장의 PNG 해제·흑백 변환을 반복하는 대신 새 프레임만 변환한다. 큰 RGB30장 배열을 연결하고 채널 그룹을 다시 만드는 작업도 제거하고 흑백30장 출력 배열을 한 번 할당한다. 비표준 매핑·지원하지 않는 이미지 모드는 기존 변환을 사용한다. 해상도256×256·30장·100Hz·정규화·시간 순서·픽셀 값은 유지한다. 각 worker의 첫16개 최적화 영상은 기존 함수와 bit-exact 비교하며 불일치 시 종료한다.

`verify_visual_cache.py`는 순차 스택·초기화·역순·RGB/비표준 매핑·출력 변경에 따른 캐시 오염·메모리 제한·PNG 청크를 검증한다. 실제3단계 입력은 `probe_stage3_cached.py`에서 기존 함수와 매번 비교한다. 변환만의 속도와 전체 학습 속도는 다르다. 전체 학습 가속·장기 성능은 실제 실행에서 측정해야 한다.

## 타자 모델과 수동 3단계

기존 타이밍 학습을162,901스텝에 정상 저장하고 별도 `Training/initialization/stage3_direct_camera30/checkpoint.pt`로 보존했다. SHA256은 `c5ef47708033947da2315daae99b517af2cc256502ee31c5f1c71f5c0d11c06f`다. `source.json`에 원본 실행·학습 스텝·미완료 준비·2단계 생략을 기록한다. 기존 결과와 정상 저장된 ONNX는 보존한다.

`Training/config/stage3_full_team_camera30_direct.yaml`은 타자에만 위 초기 모델을 사용하고 투수·주자·CF는 새로 학습한다. 기존 전체 준비·2단계는 명시적으로 생략한다. 몸 고정·준비 모드의 배트 높이/보상·30장·100Hz·관측/행동 크기를 유지한다. 수비는 현재 프로젝트의 고정C/1B/2B/3B·이동CF 구성이다. 타자가 배트 전체 제어·넓은 위치·5구종에서 검증된 모델이라는 의미는 아니며 조기 진입 성능은 따로 확인해야 한다.

학습 목표는 기존3단계 설정 그대로 타자1,200만·투수26만·주자200만·수비300만스텝이다. 영상 최적화로 학습량이 줄거나 완료가 빨리 보장되지는 않는다. 기존 준비→2→3 자동 대기는 취소했고 장기3단계 실행은 시작하지 않았다.

PowerShell에서 첫 실행:

```powershell
cd "C:\MainScreen\Dev\GitDirectory\unity_RL_baseball_agent"
& "C:\miniconda3\envs\mlagents\python.exe" -u -B Training/train_cached.py Training/config/stage3_full_team_camera30_direct.yaml --run-id=stage3_full_team_camera30_direct --results-dir=Training/results --env=Training/builds/Stage3_Camera30/Stage3_Camera30.exe --base-port=57384 --env-args -batchmode -force-d3d11
```

같은 실행을 정상 저장한 뒤 이어서 실행하려면 위 명령의 `--env-args` **앞에** `--resume`을 추가한다(뒤에 붙이면 Unity 실행 인자가 된다). `train_cached.py`는 재개 시 모든init_path를 해제해3단계 저장 모델을 읽으며1단계 초기값으로 되돌아가지 않는다. `--force`로 기존 결과를 덮어쓰지 않는다. 그래픽을 유지해야 하므로 `--no-graphics`를 붙이지 않는다. 중단은 콘솔Ctrl+C 후 체크포인트/ONNX 저장과 프로세스 종료를 기다린다.

검증 및 짧은 학습 시험 결과는 `Training/evaluations/visual_cache_stage3_20261009`와 [검증 기록](verification.md)에 남긴다. 장기 학습은 별도 `Training/results/stage3_full_team_camera30_direct`에 기록한다.
