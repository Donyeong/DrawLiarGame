# 코딩 규칙

- 답변·설명·필요한 주석은 한국어로 작성한다.
- 클래스·메서드·public 멤버는 `PascalCase`, private 필드는 `_camelCase`, 상수는 `UPPER_SNAKE_CASE`를 사용한다.
- Inspector 노출은 `[SerializeField] private`, 외부 조회는 읽기 전용 프로퍼티로 한다. 공용 JSON 계약은 `PascalCase` public 필드로 정의한다.
- 비동기는 `async/await`와 취소 토큰을 사용하고, Unity 이벤트 외의 `async void`는 피한다. 이벤트 구독과 해제를 짝짓는다.
- PostgreSQL 식별자는 큰따옴표로 감싸고, SQL 값은 매개변수로 전달한다.
