﻿using Masuit.MyBlogs.Core.Extensions;
using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using Dispose.Scope;
using EFCoreSecondLevelCacheInterceptor;
using Masuit.MyBlogs.Core.Models;

namespace Masuit.MyBlogs.Core.Controllers;

/// <summary>
/// 站内搜索
/// </summary>
public sealed class SearchController : BaseController
{
    public ISearchDetailsService SearchDetailsService { get; set; }

    /// <summary>
    /// 搜索页
    /// </summary>
    /// <param name="postService"></param>
    /// <param name="wd"></param>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    [HttpGet("search/{**wd}"), HttpGet("search", Order = 2), HttpGet("s/{**wd}", Order = 3), HttpGet("s", Order = 4)]
    public async Task<ActionResult> Search([FromServices] IPostService postService, string wd = "", [Range(1, int.MaxValue, ErrorMessage = "页码必须大于0")] int page = 1, [Range(1, 50, ErrorMessage = "页大小必须在0到50之间")] int size = 15)
    {
        wd = ChineseConverter.Convert(wd?.Trim() ?? "", ChineseConversionDirection.TraditionalToSimplified);
        if (wd.Length > 128)
        {
            wd = wd[..128];
        }

        ViewBag.PageSize = size;
        ViewBag.Keyword = wd;
        if (!wd.IsNullOrEmpty())
        {
            var posts = postService.SearchPage(PostBaseWhere(), page, size, wd);
            if (posts.Results.Count > 1)
            {
                ViewBag.Ads = AdsService.GetByWeightedPrice(AdvertiseType.ListItem, Request.Location(), keywords: wd);
            }

            ViewBag.hotSearches = new List<KeywordsRank>();
            if (posts.Total > size)
            {
                ViewBag.RelateKeywords = SearchDetailsService.GetQuery(s => s.Keywords.Contains(wd) && s.Keywords != wd).Select(s => s.Keywords).GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(10).Cacheable(CacheExpirationMode.Absolute, TimeSpan.FromHours(1)).ToPooledListScope();
            }

            if (!HttpContext.Session.TryGetValue("search:" + wd, out _) && !Request.IsRobot())
            {
                SearchDetailsService.AddEntity(new SearchDetails
                {
                    Keywords = wd,
                    SearchTime = DateTime.Now,
                    IP = ClientIP.ToString(),
                    Elapsed = posts.Elapsed,
                    ResultCount = posts.Total
                });
                await SearchDetailsService.SaveChangesAsync();
                HttpContext.Session.Set("search:" + wd, wd);
            }

            if (page == 1 && posts.Results.Count == 1 && (posts.Results[0].Title.Contains(wd) || posts.Results[0].Content.Contains(wd)))
            {
                return RedirectToAction("Details", "Post", new { id = posts.Results[0].Id, kw = wd });
            }

            return View(posts);
        }

        ViewBag.hotSearches = RedisHelper.Get<List<KeywordsRank>>("SearchRank:Week").Take(10).ToList();
        return View(new SearchResult<PostDto>());
    }

    /// <summary>
    /// 关键词搜索记录
    /// </summary>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <param name="search"></param>
    /// <returns></returns>
    [MyAuthorize, HttpPost("search/SearchList"), ResponseCache(Duration = 600, VaryByQueryKeys = new[] { "page", "size", "search" }), DistributedLockFilter]
    public ActionResult SearchList([Range(1, int.MaxValue, ErrorMessage = "页码必须大于0")] int page = 1, [Range(1, 200, ErrorMessage = "页大小必须在0到50之间")] int size = 15, string search = "")
    {
        var where = string.IsNullOrEmpty(search) ? (Expression<Func<SearchDetails, bool>>)(s => true) : s => s.Keywords.Contains(search);
        var pages = SearchDetailsService.GetQuery(where, s => s.SearchTime, false).ProjectDto().ToPagedListNoLock(page, size);
        return Ok(pages);
    }

    /// <summary>
    /// 热词
    /// </summary>
    /// <returns></returns>
    [MyAuthorize, Route("search/HotKey"), ResponseCache(Duration = 600)]
    public ActionResult HotKey()
    {
        return ResultData(new
        {
            month = RedisHelper.Get<List<KeywordsRank>>("SearchRank:Month"),
            week = RedisHelper.Get<List<KeywordsRank>>("SearchRank:Week"),
            today = RedisHelper.Get<List<KeywordsRank>>("SearchRank:Today"),
            wish_month = RedisHelper.Get<List<KeywordsRank>>("SearchWishRank:Month"),
            wish_week = RedisHelper.Get<List<KeywordsRank>>("SearchWishRank:Week"),
            wish_today = RedisHelper.Get<List<KeywordsRank>>("SearchWishRank:Today")
        });
    }

    /// <summary>
    /// 删除搜索记录
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpPost, MyAuthorize, DistributedLockFilter]
    public async Task<ActionResult> Delete(int id)
    {
        bool b = await SearchDetailsService.DeleteByIdAsync(id) > 0;
        return ResultData(null, b, b ? "删除成功！" : "删除失败！");
    }
}