using Microsoft.VisualStudio.TestTools.UnitTesting;
using CSRedis;
using System;
using System.Collections.Generic;
using System.Text;

namespace CSRedis.Tests
{
    [TestClass()]
    public class CSRedisClientTests : TestBase
    {
        [TestMethod()]
        public void SetTest()
        {
            rds.Set("2022:martyzane:language", ".net");
            string result = rds.Get("2022:martyzane:language");
            Assert.AreEqual(".net", result);

            rds.Set("2022:martyzane:demo", 1,60);
            rds.SetNx("2022:martyzane:demo-a", 2);
        }
    }
}