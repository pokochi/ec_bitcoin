using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;

namespace WebApplication1.Controllers
{
    public class AdminController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult generateKyes()
        {
            var masterData = new Dictionary<string, string>();

            for (var i = 1; i <= 3; i++)
            {
                // NBitcoinのライブラリ
                var mnemonicObj = new Mnemonic(Wordlist.English);
                var extkey = mnemonicObj.DeriveExtKey();

                // ニーモニックと拡張公開鍵を生成
                masterData.Add(
                    mnemonicObj.ToString(),
                    extkey.GetWif(Network.TestNet).Neuter().ToString()
                );

            }

            ViewBag.masterData = masterData;

            return View("Index");
        }
    }
}