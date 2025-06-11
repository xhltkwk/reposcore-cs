using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNetEnv;
using System.Text.Json;

// GitHub 저장소 데이터를 수집하는 클래스입니다.
// 저장소의 PR 및 이슈 데이터를 분석하고, 사용자별 활동 정보를 정리합니다.
/// <summary>
/// GitHub 저장소에서 이슈 및 PR 데이터를 수집하고 사용자별 활동 내역을 생성하는 클래스입니다.
/// </summary>
/// <remarks>
/// 이 클래스는 Octokit 라이브러리를 사용하여 GitHub API로부터 데이터를 가져오며,
/// 사용자 활동을 분석해 <see cref="UserActivity"/> 형태로 정리합니다.
/// </remarks>
/// <param name="owner">GitHub 저장소 소유자 (예: oss2025hnu)</param>
/// <param name="repo">GitHub 저장소 이름 (예: reposcore-cs)</param>
public class RepoDataCollector
{
    private static GitHubClient? _client; // GitHub API 요청에 사용할 클라이언트입니다.
    private readonly string _owner; // 분석 대상 저장소의 owner (예: oss2025hnu)
    private readonly string _repo; // 분석 대상 저장소의 이름 (예: reposcore-cs)
    private readonly bool _showApiLimit; // API 한도(RateLimit) 정보를 출력할지 여부

    //수정에 용이하도록 수집데이터종류 전역변수화
    private static readonly string[] FeatureLabels = { "bug", "enhancement" };
    private static readonly string[] DocsLabels = { "documentation" };
    private static readonly string TypoLabel = "typo";

    public RepoStateSummary StateSummary { get; private set; } =
        new RepoStateSummary(0, 0, 0, 0);

    // 생성자에는 저장소 하나의 정보를 넘김
    public RepoDataCollector(string owner, string repo, bool showApiLimit = false)
    {
        _owner = owner;
        _repo = repo;
        _showApiLimit = showApiLimit;
    }

    // GitHubClient 초기화 메소드
    public static void CreateClient(string? token = null)
    {
        _client = new GitHubClient(new ProductHeaderValue("reposcore-cs"));

        // 인증키 추가 (토큰이 있을경우)
        // 토큰이 직접 전달된 경우: .env 갱신 후 인증 설정
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                File.WriteAllText(".env", $"GITHUB_TOKEN={token}\n");
                Console.WriteLine(".env의 토큰을 갱신합니다.");
            }
            catch (IOException ioEx)
            {
                Console.WriteLine($"❗ .env 파일 쓰기 중 IO 오류가 발생했습니다: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Console.WriteLine($"❗ .env 파일 쓰기 권한이 없습니다: {uaEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❗ .env 파일 쓰기 중 알 수 없는 오류가 발생했습니다: {ex.Message}");
            }

            _client.Credentials = new Credentials(token);
        }
        else if (File.Exists(".env"))
        {
            try
            {
                Console.WriteLine(".env의 토큰으로 인증을 진행합니다.");
                Env.Load();
                token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

                    if (string.IsNullOrEmpty(token))
                    {
                        Console.WriteLine("❗ .env 파일에는 GITHUB_TOKEN이 포함되어 있지 않습니다.");
                    }
                    else
                    {
                        _client.Credentials = new Credentials(token);
                    }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❗ .env 파일 로딩 중 오류가 발생했습니다: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("❗ 인증 토큰이 제공되지 않았고 .env 파일도 존재하지 않습니다. 인증이 실패할 수 있습니다.");
        }
    }

    // 캐시 파일 경로를 반환하는 메서드
    private string GetCacheFilePath()
    {
        return Path.Combine("cache", $"{_owner}_{_repo}.json");
    }

    // 캐시에서 데이터를 로드하는 메서드
    private Dictionary<string, UserActivity>? LoadFromCache()
    {
        var cachePath = GetCacheFilePath();
        if (!File.Exists(cachePath))
            return null;

        try
        {
            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize<Dictionary<string, UserActivity>>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❗ 캐시 파일 로드 중 오류 발생: {ex.Message}");
            return null;
        }
    }

    // 데이터를 캐시에 저장하는 메서드
    private void SaveToCache(Dictionary<string, UserActivity> data)
    {
        var cachePath = GetCacheFilePath();
        try
        {
            var json = JsonSerializer.Serialize(data);
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❗ 캐시 파일 저장 중 오류 발생: {ex.Message}");
        }
    }

    /// <summary>
    /// 지정된 저장소의 이슈 및 PR 데이터를 수집하여 사용자별 활동 내역을 반환합니다.
    /// </summary>
    /// <param name="returnDummyData">더미 데이터를 사용할지 여부 (테스트 용도)</param>
    /// <param name="since">이 날짜 이후의 PR 및 이슈만 분석 (YYYY-MM-DD 형식)</param>
    /// <param name="until">이 날짜까지의 PR 및 이슈만 분석 (YYYY-MM-DD 형식)</param>
    /// <param name="useCache">캐시를 사용할지 여부</param>
    /// <returns>
    /// 사용자 로그인명을 키로 하고 활동 내역(UserActivity)을 값으로 갖는 Dictionary
    /// </returns>
    /// <exception cref="RateLimitExceededException">API 호출 한도 초과 시</exception>
    /// <exception cref="AuthorizationException">인증 실패 시</exception>
    /// <exception cref="NotFoundException">저장소를 찾을 수 없을 경우</exception>
    /// <exception cref="Exception">기타 알 수 없는 예외 발생 시</exception>
    // Collect 메소드
    public Dictionary<string, UserActivity> Collect(bool returnDummyData = false, string? since = null, string? until = null, bool useCache = false)
    {
        if (returnDummyData)
        {
            return DummyData.repo1Activities;
        }

        // 캐시 사용 옵션이 활성화된 경우 캐시에서 데이터 로드 시도
        if (useCache)
        {
            var cachedData = LoadFromCache();
            if (cachedData != null)
            {
                Console.WriteLine($"✅ 캐시에서 데이터를 로드했습니다: {_owner}/{_repo}");
                return cachedData;
            }
        }

        try
        {
            // Issues수집 (RP포함)
            var request = new RepositoryIssueRequest
            {
                State = ItemStateFilter.All
            };

            if (!string.IsNullOrEmpty(since))
            {
                if (DateTime.TryParse(since, out DateTime sinceDate))
                {
                    request.Since = sinceDate;
                }
                else
                {
                    throw new ArgumentException($"잘못된 시작 날짜 형식입니다: {since}. YYYY-MM-DD 형식으로 입력해주세요.");
                }
            }

            var allIssuesAndPRs = _client!.Issue.GetAllForRepository(_owner, _repo, request).Result;

            // until 날짜 필터링 적용
            if (!string.IsNullOrEmpty(until))
            {
                if (!DateTime.TryParse(until, out DateTime untilDate))
                {
                    throw new ArgumentException($"잘못된 종료 날짜 형식입니다: {until}. YYYY-MM-DD 형식으로 입력해주세요.");
                }
                allIssuesAndPRs = allIssuesAndPRs.Where(issue => issue.CreatedAt <= untilDate).ToList();
            }

            // 수집용 mutable 객체. 모든 데이터 수집 후 레코드로 변환하여 반환
            var mutableActivities = new Dictionary<string, UserActivity>();
            int mergedPr = 0;
            int unmergedPr = 0;
            int openIssue = 0;
            int closedIssue = 0;

            // allIssuesAndPRs의 데이터를 유저,라벨별로 분류
            foreach (var item in allIssuesAndPRs)
            {
                if (item.User?.Login == null) continue;

                var username = item.User.Login;

                // 처음 기록하는 사용자 초기화
                if (!mutableActivities.ContainsKey(username))
                {
                    mutableActivities[username] = new UserActivity(0,0,0,0,0);
                }

                var labelName = item.Labels.Any() ? item.Labels[0].Name : null; // 라벨 구분을 위한 labelName

                var activity = mutableActivities[username];

                if (item.PullRequest != null) // PR일 경우
                {
                    try
                    {
                        var pr = _client.PullRequest.Get(_owner, _repo, item.Number).Result;
                        if (pr.Merged == true)
                        {
                            mergedPr++;
                            if (FeatureLabels.Contains(labelName))
                                activity.PR_fb++;
                            else if (DocsLabels.Contains(labelName))
                                activity.PR_doc++;
                            else if (labelName == TypoLabel)
                                activity.PR_typo++;
                        }
                        else
                        {
                            unmergedPr++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❗ PR #{item.Number} 정보를 가져오는 중 오류 발생: {ex.Message}");
                    }
                }
                else
                {
                    if (item.State.Value.ToString() == "Open")
                    {
                        openIssue++;
                        if (FeatureLabels.Contains(labelName))
                            activity.IS_fb++;
                        else if (DocsLabels.Contains(labelName))
                            activity.IS_doc++;
                    }
                    else if (item.State.Value.ToString() == "Closed")
                    {
                        closedIssue++;
                        if (item.StateReason?.ToString() == "completed")
                        {
                            if (FeatureLabels.Contains(labelName))
                                activity.IS_fb++;
                            else if (DocsLabels.Contains(labelName))
                                activity.IS_doc++;
                        }
                    }
                }
            }

            // 레코드로 변환
            var userActivities = new Dictionary<string, UserActivity>();
            foreach (var (key, value) in mutableActivities)
            {
                userActivities[key] = new UserActivity(
                    PR_fb: value.PR_fb,
                    PR_doc: value.PR_doc,
                    PR_typo: value.PR_typo,
                    IS_fb: value.IS_fb,
                    IS_doc: value.IS_doc
                );
            }

            StateSummary = new RepoStateSummary(mergedPr, unmergedPr, openIssue, closedIssue);

            // 데이터 수집 성공 시 캐시에 저장
            SaveToCache(userActivities);

            return userActivities;
        }
        catch (RateLimitExceededException)
        {
            try
            {
                var rateLimits = _client!.RateLimit.GetRateLimits().Result;
                var coreRateLimit = rateLimits.Rate;
                var resetTime = coreRateLimit.Reset; // UTC DateTime
                var secondsUntilReset = (int)(resetTime - DateTimeOffset.UtcNow).TotalSeconds;

                Console.WriteLine($"❗[{_owner}/{_repo}] API 호출 한도(Rate Limit)를 초과했습니다. {secondsUntilReset}초 후 재시도 가능합니다 (약 {resetTime.LocalDateTime} 기준).");
            }
            catch (Exception innerEx)
            {
                Console.WriteLine($"❗[{_owner}/{_repo}] API 호출 한도 초과, 재시도 시간을 가져오는 데 실패했습니다: {innerEx.Message}");
            }

            Environment.Exit(1);
        }
        catch (AuthorizationException)
        {
            Console.WriteLine($"❗[{_owner}/{_repo}] 인증 실패: 올바른 토큰을 사용했는지 확인하세요.");
            Environment.Exit(1);
        }
        catch (NotFoundException)
        {
            Console.WriteLine($"❗[{_owner}/{_repo}] 저장소를 찾을 수 없습니다. owner/repo 이름을 확인하세요.");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❗[{_owner}/{_repo}] 알 수 없는 오류가 발생했습니다: {ex.Message}");
            Environment.Exit(1);
        }
        return null!;
    }
}
