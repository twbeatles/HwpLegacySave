# CLR 프로파일링 헤더

`corprof.h`, `cor.h`, `corhdr.h`, `corerror.h`는 `dotnet/runtime`에서 가져왔다. 프로파일러는 `cor.h` 다음에 `corprof.h`를 포함한다. `corhdr.h`의 `IMAGE_COR_ILMETHOD_TINY`를 사용해 ReJIT에 넘길 3바이트 메서드 본문을 구성한다. `LICENSE.TXT`는 해당 헤더의 MIT 라이선스 고지다.

```sh
TAG=v8.0.11
BASE=https://raw.githubusercontent.com/dotnet/runtime/$TAG
curl -fSs -O "$BASE/src/coreclr/pal/prebuilt/inc/corprof.h"
curl -fSs -O "$BASE/src/coreclr/pal/prebuilt/inc/corerror.h"
curl -fSs -O "$BASE/src/coreclr/inc/cor.h"
curl -fSs -O "$BASE/src/coreclr/inc/corhdr.h"
curl -fSs -O "$BASE/LICENSE.TXT"
```
