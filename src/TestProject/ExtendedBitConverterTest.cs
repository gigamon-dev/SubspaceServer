using SS.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace TestProject
{
    
    
    /// <summary>
    ///This is a test class for ExtendedBitConverterTest and is intended
    ///to contain all ExtendedBitConverterTest Unit Tests
    ///</summary>
    [TestClass()]
    public class ExtendedBitConverterTest
    {


        private TestContext testContextInstance;

        /// <summary>
        ///Gets or sets the test context which provides
        ///information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get
            {
                return testContextInstance;
            }
            set
            {
                testContextInstance = value;
            }
        }

        #region Additional test attributes
        // 
        //You can use the following additional attributes as you write your tests:
        //
        //Use ClassInitialize to run code before running the first test in the class
        //[ClassInitialize()]
        //public static void MyClassInitialize(TestContext testContext)
        //{
        //}
        //
        //Use ClassCleanup to run code after all tests in a class have run
        //[ClassCleanup()]
        //public static void MyClassCleanup()
        //{
        //}
        //
        //Use TestInitialize to run code before running each test
        //[TestInitialize()]
        //public void MyTestInitialize()
        //{
        //}
        //
        //Use TestCleanup to run code after each test has run
        //[TestCleanup()]
        //public void MyTestCleanup()
        //{
        //}
        //
        #endregion


        /// <summary>
        ///A test for ToUInt32
        ///</summary>
        [TestMethod()]
        public void ToUInt32Test1()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            uint expected = 0; // TODO: Initialize to an appropriate value
            uint actual;
            actual = ExtendedBitConverter.ToUInt32(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToUInt32
        ///</summary>
        [TestMethod()]
        public void ToUInt32Test()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            int numBits = 0; // TODO: Initialize to an appropriate value
            uint expected = 0; // TODO: Initialize to an appropriate value
            uint actual;
            actual = ExtendedBitConverter.ToUInt32(data, byteOffset, bitOffset, numBits);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToUInt16
        ///</summary>
        [TestMethod()]
        public void ToUInt16Test()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            ushort expected = 0; // TODO: Initialize to an appropriate value
            ushort actual;
            actual = ExtendedBitConverter.ToUInt16(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToSByte
        ///</summary>
        [TestMethod()]
        public void ToSByteTest1()
        {
            byte[] data = { 0x00, 0x01, 0xFE };
            int byteOffset = 1;
            int bitOffset = 7;
            sbyte expected = -1;
            sbyte actual;

            actual = ExtendedBitConverter.ToSByte(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            actual = ExtendedBitConverter.ToSByte(data, byteOffset, bitOffset, 8);
            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        ///A test for ToSByte
        ///</summary>
        [TestMethod()]
        public void ToSByteTest()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            int numBits = 0; // TODO: Initialize to an appropriate value
            sbyte expected = 0; // TODO: Initialize to an appropriate value
            sbyte actual;
            actual = ExtendedBitConverter.ToSByte(data, byteOffset, bitOffset, numBits);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToInt32
        ///</summary>
        [TestMethod()]
        public void ToInt32Test()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            int expected = 0; // TODO: Initialize to an appropriate value
            int actual;
            actual = ExtendedBitConverter.ToInt32(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToInt16
        ///</summary>
        [TestMethod()]
        public void ToInt16Test()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            short expected = 0; // TODO: Initialize to an appropriate value
            short actual;
            actual = ExtendedBitConverter.ToInt16(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToByte
        ///</summary>
        [TestMethod()]
        public void ToByteTest1()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            int numBits = 0; // TODO: Initialize to an appropriate value
            byte expected = 0; // TODO: Initialize to an appropriate value
            byte actual;
            actual = ExtendedBitConverter.ToByte(data, byteOffset, bitOffset, numBits);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ToByte
        ///</summary>
        [TestMethod()]
        public void ToByteTest()
        {
            byte[] data = null; // TODO: Initialize to an appropriate value
            int byteOffset = 0; // TODO: Initialize to an appropriate value
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            byte expected = 0; // TODO: Initialize to an appropriate value
            byte actual;
            actual = ExtendedBitConverter.ToByte(data, byteOffset, bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for getBitOffsetMask
        ///</summary>
        [TestMethod()]
        [DeploymentItem("SS.Utilities.dll")]
        public void getBitOffsetMaskTest()
        {
            int bitOffset = 0; // TODO: Initialize to an appropriate value
            byte expected = 0; // TODO: Initialize to an appropriate value
            byte actual;
            actual = ExtendedBitConverter_Accessor.getBitOffsetMask(bitOffset);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }
    }
}
