using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QBitNinja.Client;
using QBitNinja.Client.Models;

namespace WebApplication1.Controllers
{
    public class ShopController : Controller
    {
        // コールド環境で生成した拡張公開鍵
        ReadOnlyCollection<string> extPubKeys = Array.AsReadOnly(
            new string[]
            {
                "tpubD6NzVbkrYhZ4WQjZdFnx3ZFeiiCJtxJ7c8Qv1nfRSuPiuHgVsHL61jTbxbE7zUfnp6Kjz23WJcdEqN4zGupKMu1rYu7yWZ6kjpTBuugqQfR"
                ,"tpubD6NzVbkrYhZ4YM3imKgBHwdAUKJLDd9iEHitPhktjtepqMQRHgjdYH99Bpv9LA1CfZfUidsVSHqtzvSwA7LkjyvjULcKvXBEd714CJY5hRT"
                ,"tpubD6NzVbkrYhZ4XQbpRKZhvJoAF7gRZUkz9quLPb9b1p38RoN9fTS6WGKS9T9RY9rVzJZNd7aGgxdMLDsgvN7aLN6pCFo4FTpstErjfXRj9GK"
            }
        );

        public IActionResult Index()
        {
            return View();
        }

        //オーダー毎の支払アドレスを生成してQRコードを表示する
        [HttpPost]
        public IActionResult generatePayAdress([FromForm] uint orderId)
        {
            var scriptMultiSig = generatePayment(extPubKeys, orderId);
            /**
             * P2WSHをP2SHへネストしたアドレス（※1　ちゃんとネストできてる？）
             * P2SH -> P2WSH -> P2MULTISIG
             */
            var bitcoinAdress = scriptMultiSig.WitHash.ScriptPubKey.Hash.ScriptPubKey.Hash.GetAddress(Network.TestNet);

            ViewBag.BitcoinAdress = bitcoinAdress;
            ViewBag.QrUrl = "https://chart.apis.google.com/chart?chs=500x500&cht=qr&chl=bitcoin:" + bitcoinAdress;

            return View("Index");
        }

        // 売上回収トランザクション
        [HttpPost]
        public IActionResult withdrawal([FromForm] uint orderId)
        {
            // コールド環境で生成したニーモニック
            ReadOnlyCollection<string> mnemonics = Array.AsReadOnly(
                new string[]
                {
                    "melody swap what surround atom figure arrange clown wolf unit wait fiscal trophy wreck pizza behind venture example unfold survey tank tired sorry used"
                    ,"other upon make bonus avocado better useful funny someone camera lava odor brain cigar sell output love salad draw loop all carry south game"
                }
            );

            var privateKeys    = mnemonics.Select(value => GetPreKey(value, orderId));
            var scriptMultiSig = generatePayment(extPubKeys, orderId);
            var bitcoinAdress  = scriptMultiSig.WitHash.ScriptPubKey.Hash.ScriptPubKey.Hash.GetAddress(Network.TestNet);

            // アドレスに紐づくUTXOを取得
            var requestUrl = string.Format("https://chain.so/api/v2/get_tx_unspent/BTCTEST/{0}", bitcoinAdress);
            var req = WebRequest.Create(requestUrl);
            StreamReader stReader = new StreamReader(req.GetResponse().GetResponseStream(), new UTF8Encoding(true));

            // 結果のJSONをパース
            var jsonData = JObject.Parse(@stReader.ReadToEnd().ToString());
            jsonData["data"]["txs"].Select(tx => tx["value"] = 100000000 * (double)tx["value"]);

            var client = new QBitNinjaClient(Network.TestNet);
            var transaction = Transaction.Create(Network.TestNet);
            decimal balance = 0;
            foreach (var utxo in jsonData["data"]["txs"])
            {
                // txidからトランザクション取得（※2　chain.soから取得したtxidをそのままParseして大丈夫か？）
                var transactionId = uint256.Parse(utxo["txid"].ToString());
                var transactionResponse = client.GetTransaction(transactionId).Result;

                foreach (var coin in transactionResponse.ReceivedCoins)
                {
                    TxIn txIn = new TxIn()
                    {
                        PrevOut = coin.Outpoint
                    };

                    transaction.Inputs.Add(txIn);
                }

                balance += (decimal)utxo["value"];
            }

            // 売上回収用のビットコインアドレス
            var recciveAdress = BitcoinAddress.Create("2N8GaKiU895NHjMX5FxhwncaR94k9iuQZfn");
            // アウトプットを作成
            TxOut txOut = new TxOut()
            {
                Value = new Money(balance, MoneyUnit.BTC),
                ScriptPubKey = recciveAdress.ScriptPubKey.PaymentScript
            };

            transaction.Outputs.Add(txOut);
            
            // マルチシグの署名　（※3　この実装で合ってる？）
            foreach (var privateKey in privateKeys)
            {
                transaction.Sign(privateKey.PrivateKey, transaction.Outputs.AsCoins().First());
            }

            BroadcastResponse broadcastResponse = client.Broadcast(transaction).Result;
            if (!broadcastResponse.Success)
            {
                ViewBag.errorCode = "ErrorCode: " + broadcastResponse.Error.ErrorCode;
                ViewBag.errorMessage = "Error message: " + broadcastResponse.Error.Reason;
            }

            ViewBag.errorCode = "OK";
            ViewBag.errorMessage = "";
            ViewBag.hex = transaction.ToHex();
            ViewBag.broadcast = transaction.GetHash();

            return View("Index");
        }

        private static Script generatePayment(ReadOnlyCollection<string> extPubKeys, uint orderId)
        {
            IEnumerable<PubKey> IpubKeys = extPubKeys.Select(value => GetPubKey(value, orderId));

            var Keys = new List<PubKey>();
            foreach (PubKey p in IpubKeys)
            {
                Keys.Add(p);
            }

            var script = PayToMultiSigTemplate
                .Instance
                .GenerateScriptPubKey(
                    2,
                    Keys.ToArray()
                );

            return script;
        }

        private static PubKey GetPubKey(string value, uint orderId)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(value);
            var extKey = new ExtKey(seed: data);

            return extKey.Derive(orderId).Neuter().PubKey;
        }

        private static BitcoinSecret GetPreKey(string value, uint orderId)
        {
            Mnemonic mnemonic = new Mnemonic(value, Wordlist.English);
            ExtKey extKey = mnemonic.DeriveExtKey();

            return extKey.Derive(orderId).PrivateKey.GetWif(Network.TestNet);
        }
    }
}
