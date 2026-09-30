. (Join-Path $PSScriptRoot '_common.ps1')

# 설정 저장소(AppConfig). 계정 정보를 한 번 저장하면 앱을 껐다 켜도 유지돼야 하고,
# 비밀번호는 파일에 평문으로 남아서는 안 된다.
# Start-Test 가 FILECRYPT_DATA_DIR 를 작업 폴더로 돌려 두므로 실제 %LOCALAPPDATA%\FileCrypt 는 건드리지 않는다.

Start-Test -Tag cfg -Title '설정 저장소 (AppConfig)' -Pad 50
Import-FileCrypt
$CFG = [FileCrypt.AppConfig]
$cfgFile = $CFG::File_

# ================================================================ 0) 격리
$realDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileCrypt'
Ok '환경변수 이름이 FILECRYPT_DATA_DIR' ($CFG::DirEnvVar -eq 'FILECRYPT_DATA_DIR') $CFG::DirEnvVar
Ok '설정 폴더가 작업 폴더 아래 (실제 설정 보호)' `
   ($CFG::Dir.StartsWith($WORK, [StringComparison]::OrdinalIgnoreCase)) $CFG::Dir
Ok '  실제 %LOCALAPPDATA%\FileCrypt 가 아님' (-not $CFG::Dir.Equals($realDir, [StringComparison]::OrdinalIgnoreCase)) ''
Ok '  설정 파일도 그 안에' ($cfgFile.StartsWith($CFG::Dir, [StringComparison]::OrdinalIgnoreCase)) ''

if (Test-Path -LiteralPath $cfgFile) { Remove-Item -LiteralPath $cfgFile -Force }
$CFG::Reload()

# ================================================================ 1) 빈 상태
Ok '설정 없음 -> 계정 없음으로 본다' (-not $CFG::HasNetcusAccount) ''
Ok '  아이디는 빈 문자열' ($CFG::NetcusId -eq '') ''
Ok '  한도는 기본값(20만)' ($CFG::NetcusLimit -eq 200000) ('{0:N0}' -f $CFG::NetcusLimit)

# ================================================================ 2) 저장 -> 유지
$CFG::NetcusId = '  hjlee  '          # 앞뒤 공백은 정리돼야 한다
$CFG::NetcusPassword = 'p@ss word!한글123'
Ok '아이디 저장(공백 정리)' ($CFG::NetcusId -eq 'hjlee') ("'" + $CFG::NetcusId + "'")
Ok '비밀번호 왕복' ($CFG::NetcusPassword -eq 'p@ss word!한글123') ''
Ok '계정 있음으로 바뀜' ($CFG::HasNetcusAccount) ''

# 파일을 다시 읽어도(=앱 재시작) 그대로여야 한다
$CFG::Reload()
Ok '재시작해도 아이디 유지' ($CFG::NetcusId -eq 'hjlee') ''
Ok '재시작해도 비밀번호 유지' ($CFG::NetcusPassword -eq 'p@ss word!한글123') ''

# ================================================================ 3) 비밀번호가 평문으로 남지 않는다
$raw = [System.IO.File]::ReadAllText($cfgFile)
Ok '파일에 비밀번호 평문이 없음' (-not $raw.Contains('p@ss word!')) ''
Ok '  아이디는 평문으로 보임(정상)' ($raw.Contains('hjlee')) ''

# ================================================================ 4) 로그인 확인 시각
Ok '확인 시각 처음엔 없음' ($null -eq $CFG::NetcusVerifiedAt) ''
$now = [datetime]::UtcNow
$CFG::NetcusVerifiedAt = $now
$CFG::Reload()
$got = $CFG::NetcusVerifiedAt
Ok '확인 시각 저장/복원(초 단위 일치)' `
   (($null -ne $got) -and ([Math]::Abs(($got - $now).TotalSeconds) -lt 1)) `
   $(if($got){$got.ToString('s')}else{'null'})

# ================================================================ 5) 한도 설정
$CFG::NetcusLimit = 50000
$CFG::Reload()
Ok '한도 저장/복원' ($CFG::NetcusLimit -eq 50000) ('{0:N0}' -f $CFG::NetcusLimit)
$CFG::NetcusLimit = 10        # 너무 작은 값은 무시하고 기본값으로
$CFG::Reload()
Ok '말도 안 되는 한도는 기본값으로' ($CFG::NetcusLimit -eq 200000) ('{0:N0}' -f $CFG::NetcusLimit)

# ================================================================ 5-2) 날짜 사이 쉬는 시간 (netcus.paceMs)
# 사이트 요청 속도 제한 때문에 기본은 700ms. 설정으로 줄일 수 있지만 범위(0~10000) 밖은 무시한다.
Ok '쉬는 시간 기본값 700ms' ($CFG::NetcusPaceMs -eq 700) ('{0}' -f $CFG::NetcusPaceMs)
$CFG::NetcusPaceMs = 300
$CFG::Reload()
Ok '  300 저장/복원' ($CFG::NetcusPaceMs -eq 300) ('{0}' -f $CFG::NetcusPaceMs)
$CFG::NetcusPaceMs = 20000
$CFG::Reload()
Ok '  범위 밖(20000)은 기본값 700 으로' ($CFG::NetcusPaceMs -eq 700) ('{0}' -f $CFG::NetcusPaceMs)
$CFG::Set('netcus.paceMs', '빠르게')
$CFG::Reload()
Ok '  숫자가 아니면 기본값 700 으로' ($CFG::NetcusPaceMs -eq 700) ('{0}' -f $CFG::NetcusPaceMs)

# ================================================================ 6) 계정만 삭제
$CFG::NetcusLimit = 300000
$CFG::ClearNetcusAccount()
$CFG::Reload()
Ok '계정 삭제됨' (-not $CFG::HasNetcusAccount) ''
Ok '  비밀번호도 사라짐' ($CFG::NetcusPassword -eq '') ''
Ok '  확인 시각도 사라짐' ($null -eq $CFG::NetcusVerifiedAt) ''
Ok '  다른 설정(한도)은 남아 있음' ($CFG::NetcusLimit -eq 300000) ('{0:N0}' -f $CFG::NetcusLimit)

# ================================================================ 6-2) 마지막에 쓴 값 기억
# 같은 날짜를 계속 쓰는 사용자가 매번 다시 고르지 않도록.
Ok '날짜 기억 없으면 null' ($null -eq $CFG::NetcusLastDate) ''
Ok '  기억이 없으면 기본 날짜를 쓴다 (오늘 아님)' `
   ($CFG::NetcusStartDate -eq $CFG::NetcusDefaultDate) ($CFG::NetcusStartDate.ToString('yyyy-MM-dd'))
Ok '  기본 날짜는 2024-08-14' ($CFG::NetcusDefaultDate.ToString('yyyy-MM-dd') -eq '2024-08-14') ''
Ok '  기본 날짜가 오늘이 아니다 (실제 근무일 보호)' `
   ($CFG::NetcusDefaultDate.Date -ne [datetime]::Today) ''

$CFG::NetcusLastDate = [datetime]'2024-08-20'
$CFG::Reload()
Ok '  기억이 있으면 그쪽이 이긴다' ($CFG::NetcusStartDate.ToString('yyyy-MM-dd') -eq '2024-08-20') `
   ($CFG::NetcusStartDate.ToString('yyyy-MM-dd'))

$CFG::NetcusLastDate = [datetime]'2024-08-14'
$CFG::Reload()
Ok '마지막 날짜 저장/복원' `
   (($null -ne $CFG::NetcusLastDate) -and ($CFG::NetcusLastDate.ToString('yyyy-MM-dd') -eq '2024-08-14')) `
   $(if($CFG::NetcusLastDate){$CFG::NetcusLastDate.ToString('yyyy-MM-dd')}else{'null'})

Ok '일수 기본값 1' ($CFG::NetcusLastDays -eq 1) ('{0}' -f $CFG::NetcusLastDays)
$CFG::NetcusLastDays = 5
$CFG::Reload()
Ok '마지막 일수 저장/복원' ($CFG::NetcusLastDays -eq 5) ('{0}' -f $CFG::NetcusLastDays)
$CFG::NetcusLastDays = 999      # 범위 밖은 무시
$CFG::Reload()
Ok '말도 안 되는 일수는 1 로' ($CFG::NetcusLastDays -eq 1) ('{0}' -f $CFG::NetcusLastDays)

$CFG::NetcusLastOutDir = $WORK
$CFG::Reload()
Ok '마지막 저장 폴더 기억' ($CFG::NetcusLastOutDir -eq $WORK) ''
$CFG::NetcusLastOutDir = 'C:\없는폴더_' + [Guid]::NewGuid().ToString('N')
$CFG::Reload()
Ok '없어진 폴더는 돌려주지 않는다' ($CFG::NetcusLastOutDir -eq '') ("'" + $CFG::NetcusLastOutDir + "'")

# 날짜 형식이 깨져 있어도 앱은 떠야 한다
$CFG::Set('netcus.lastDate', '이건날짜가아님')
$CFG::Reload()
Ok '깨진 날짜는 null 로' ($null -eq $CFG::NetcusLastDate) ''
Ok '  그래도 기본 날짜로 뜬다' ($CFG::NetcusStartDate -eq $CFG::NetcusDefaultDate) ''

# ================================================================ 7) 손상된 파일에도 앱은 떠야 한다
[System.IO.File]::WriteAllText($cfgFile, "쓰레기 줄`r`n=값만 있음`r`nnetcus.id=bob`r`n깨진줄")
$CFG::Reload()
Ok '손상된 설정에도 읽을 수 있는 값은 살림' ($CFG::NetcusId -eq 'bob') ("'" + $CFG::NetcusId + "'")
Ok '  깨진 비밀번호는 빈 값으로' ($CFG::NetcusPassword -eq '') ''

Complete-Test
