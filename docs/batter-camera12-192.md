# 타자 카메라 192×192·12장 (2026-10-09)

## 현재 관측 계약

사용자가256×256·30장을192×192·12장으로 줄이도록 요청했다. `BatterAgent.ImageWidth/ImageHeight=192`, `ImageStacks=12`이며 판단·물리는 기존10ms(100Hz)를 유지한다. 영상은 흑백이며 포수 시점·FOV·공 크기·공 전용 레이어·검은 배경·PNG 압축·과거→최신 순서·벡터16값·행동7연속+[2]를 유지한다. 타자 몸은 계속 고정이다. 새 스택은 현재 포함12장, 가장 오래된 영상은110ms 전이다.

1회 영상 원소는442,368개(float32 약1.6875MiB)로 기존1,966,080개(7.5MiB)의22.5%다. 새 영상 자체의 픽셀 수는기존의56.25%다. 전체 처리 속도나 정타율이 같은 비율로 개선된다는 의미는 아니다. Python 프레임 캐시는 새 크기에도 동일하게 적용하며 첫16회는기존 변환 함수와 배열이 완전히 같은지 확인한다.

`TrainingSceneBuilder.UpdateBatterCameraSensors`로 기존1·2·3단계 각 경기장의 센서 필드만 갱신한다. 씬을 재생성하지 않고 기존씬 GUID·오브젝트 식별자·카메라 배치를 유지한다. 이전 입력의 실행 파일과 결과는 보존하며 새 실행 파일은 `Training/builds/BatterCamera12_192`, `Stage2_Camera12_192`, `Stage3_Camera12_192`에 둔다. 열린 원본 에디터와 충돌하지 않도록 기존 임시 복제 프로젝트에서 Unity 저장·검증·빌드를 한다.

## 기존 학습의 보존과 이식

기존 `stage3_full_team_camera30_direct` 실행을 정상 Ctrl+C 경로로 종료했다. 타자는22,975스텝, 주자는1,856스텝, CF는981스텝, 투수는0스텝으로 저장됐다. 모든 원본 체크포인트·ONNX·로그는 유지한다. 타자는1단계162,901스텝 모델에서 시작한 기존3단계의 최신 가중치를 사용한다.

`resize_camera_checkpoint.py`는 타자 actor와 critic의첫CNN 30채널을12채널로 맞춘다. 기존0~18채널의 가중치는새 가장 오래된 프레임(110ms)에 합치고, 기존19~29는새1~11로 그대로 대응시킨다. 즉 최신110ms의가중치를 유지하면서 더 오래된 정보는110ms 프레임에 근사한다. 공간dense 가중치는32×30×30에서32×22×22로 bilinear 보간하고 면적 비율을 적용한다. 다른 모든학습 텐서는보존한다. 차원이 달라진Adam 모멘트는초기화한다. 이것은 **근사 warm start**이며 동일 행동·동일 정타율을 주장하지 않는다. 새 학습의스텝은0부터 시작한다.

투수·주자·CF 체크포인트는byte-identical 복사해 새초기값으로 연결한다. 0스텝 투수는학습 완료가 아니다. 고정 상대 평가의타자 모델은별도로1단계162,901스텝 원본에서 같은변환을 적용해 `BenchmarkBatter_Stage1_Camera12_192.onnx`로 만든다. 평가 모델에최신3단계 live가중치를 섞지 않는다.

## 수동 실행

장기 학습은 사용자가 직접 시작한다. Anaconda/Miniconda Prompt에서:

```bat
conda activate mlagents
cd /d "C:\MainScreen\Dev\GitDirectory\unity_RL_baseball_agent"
python -u -B Training/train_cached.py Training/config/stage3_full_team_camera12_192_direct.yaml --run-id=stage3_full_team_camera12_192_direct --results-dir=Training/results --env=Training/builds/Stage3_Camera12_192/Stage3_Camera12_192.exe --base-port=57394 --width=1280 --height=720 --time-scale=1 --capture-frame-rate=0 --target-frame-rate=60 --env-args -force-d3d11
```

위 명령은관찰용1배속·게임 창 표시다. 기존30장학습에는연결하지 않으며 `--resume`을첫시작에붙이지 않는다. 새12장실행을정상 저장한뒤재개할때만 `--env-args` **앞에** `--resume`을추가한다. 화면없이학습하려면20배속 등 학습용배속을명시하고 `--env-args` 뒤에 `-batchmode`를추가할수 있다. 카메라입력이 필요하므로 `--no-graphics`를사용하지 않는다. `--force`로기존결과를덮어쓰지 않는다.

Unity Editor에서현재3단계씬을열어Play하면새센서설정은적용되지만, 위명령의학습은별도Player에서진행한다. 에디터Play를같이실행하면별도의경기장이추가로돌아간다. 디스크에서갱신한씬은Play를종료하고다시열어확인한다. 에디터에서씬을새로생성할필요는없다.

## 검증

검증 산출물은 `Training/evaluations/camera12_192_20261009`에 기록한다. 가중치 보존·시간 채널 대응·잘못된 입력 거부·actor/critic strict load·유한한 forward와 gradient·ONNX 검사, Unity 세 단계 Play Mode와 실제 센서 공 가시성, 새Player의실제입력 배열 동일성, 별도 짧은 학습과 정상 저장을 확인한다. 장기 정타율·오랜학습 성능·화면 FPS 개선은별도로측정해야한다.
