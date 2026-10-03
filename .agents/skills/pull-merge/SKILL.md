---
name: pull-merge
description: 현재 브랜치의 원격 변경을 pull하고 충돌을 해결한다. 새 커밋이나 push 없이 원격 변경만 통합해 달라는 요청에 사용한다.
---

현재 브랜치의 upstream에서 변경을 가져오고 충돌을 해결한다. 작업 커밋과 merge commit을 만들지 않으며 push하지 않는다.

1. 현재 브랜치, upstream, staged·unstaged·untracked 변경과 진행 중인 merge/rebase를 확인한다. detached HEAD, 불명확한 원격 대상, 이미 진행 중인 병합은 임의로 처리하지 않고 필요한 정보만 질문한다.
2. 로컬 변경이 있으면 파일과 스테이징 상태를 기록하고 `git stash push --include-untracked`로 보관한다. 이번 stash의 OID를 기록하고 기존 stash와 ignored 파일은 건드리지 않는다. stash는 작업 보관용이며 브랜치 커밋을 만드는 데 사용하지 않는다.
3. `git -c merge.autoStash=false pull --no-rebase --no-commit --ff`를 실행한다. fast-forward는 허용하지만, 이력이 갈라지면 merge commit 생성 직전에 멈춘다. `--no-commit` 없이 pull하거나 rebase로 대체하지 않는다.
4. 충돌이 나면 양쪽 변경 의도와 프로젝트 규칙을 확인해 자동으로 해결하고 해결한 파일만 `git add`한다. `ours`·`theirs` 일괄 적용은 하지 않는다. 바이너리 충돌이나 상충하는 요구사항처럼 판단이 불가능한 경우 보존 상태를 유지하고 필요한 판단만 질문한다.
5. `MERGE_HEAD`가 남아 있으면 충돌 파일이 없는지 확인한 뒤 커밋 대기 상태로 종료한다. 이때 보관한 로컬 작업은 merge 결과와 섞이지 않도록 stash에 유지하고 OID를 보고한다. `git commit`, `git merge --continue`, `git rebase --continue`로 병합을 확정하지 않는다.
6. fast-forward 또는 이미 최신 상태라면 이번 stash를 `git stash apply --index`로 복원한다. 복원 충돌도 자동으로 해결하되 실패한 apply를 그대로 반복하지 않는다. 파일 내용과 원래 스테이징 상태를 확인한 후 이번 stash만 제거한다. 완전히 복원하지 못했으면 stash를 보존하고 남은 상태를 보고한다.
7. 변경에 필요한 검증을 수행하고 pull 결과, 충돌 해결 여부, 커밋 대기 여부, 보관 중인 stash를 간단히 보고한다. 충돌 해결이 끝난 것과 merge commit까지 완료된 것을 구분한다.

- `git push`, force push, `reset --hard`, 변경을 버리는 checkout/restore, `git clean`을 실행하지 않는다.
- 커밋·push는 별도 사용자 요청이 있을 때만 별개 작업으로 수행한다. 이 스킬의 실행 자체에는 해당 권한이 없다.
