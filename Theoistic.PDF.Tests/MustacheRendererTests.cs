using System.Diagnostics;

namespace Theoistic.PDF.Tests
{
    [TestClass]
    public sealed class MustacheRendererTests
    {
        public class AdyenReceiptModel
        {
            public MerchantInfo Merchant { get; set; }
            public TransactionInfo Transaction { get; set; }
            public VoucherInfo Voucher { get; set; }
        }

        public class MerchantInfo
        {
            public string Name { get; set; }
            public string ID { get; set; }
        }

        public class TransactionInfo
        {
            public string ID { get; set; }
            public string Date { get; set; } // Consider using DateTime for real implementations
            public AmountInfo Amount { get; set; }
            public string Status { get; set; }
            public List<string> Details { get; set; }
        }

        public class AmountInfo
        {
            public string Currency { get; set; }
            public decimal Value { get; set; }
        }

        public class VoucherInfo
        {
            public string Code { get; set; }
            public string ExpiryDate { get; set; } // Consider using DateTime for real implementations
        }


        [TestMethod]
        public void RenderAdyenVoucherReceipt_ShouldRenderCorrectly()
        {
            // Arrange
            string template = @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <title>Adyen Receipt - Captive Portal Voucher</title>
    <style>
        body { font-family: Arial, sans-serif; }
        .receipt { max-width: 600px; margin: auto; border: 1px solid #ccc; padding: 20px; }
        .header { text-align: center; }
        .details, .voucher, .transaction { margin-top: 20px; }
        .details th, .details td { padding: 5px 10px; text-align: left; }
        .voucher-code { font-size: 1.2em; color: #2E8B57; }
    </style>
</head>
<body>
    <div class=""receipt"">
        <div class=""header"">
            <h1>Adyen Receipt</h1>
            <h2>Captive Portal Voucher</h2>
        </div>

        <div class=""details"">
            <table>
                <tr>
                    <th>Merchant:</th>
                    <td>{{Merchant.Name}}</td>
                </tr>
                <tr>
                    <th>Merchant ID:</th>
                    <td>{{Merchant.ID}}</td>
                </tr>
                <tr>
                    <th>Transaction ID:</th>
                    <td>{{Transaction.ID}}</td>
                </tr>
                <tr>
                    <th>Date:</th>
                    <td>{{Transaction.Date}}</td>
                </tr>
                <tr>
                    <th>Amount:</th>
                    <td>{{Transaction.Amount.Currency}} {{Transaction.Amount.Value}}</td>
                </tr>
                <tr>
                    <th>Status:</th>
                    <td>{{Transaction.Status}}</td>
                </tr>
            </table>
        </div>

        {{#Voucher}}
        <div class=""voucher"">
            <h3>Your Voucher</h3>
            <p>Use the following code to access the captive portal:</p>
            <p class=""voucher-code"">{{Code}}</p>
            <p>Valid until: {{ExpiryDate}}</p>
        </div>
        {{/Voucher}}

        <div class=""transaction"">
            <h3>Transaction Details</h3>
            <ul>
                {{#Transaction.Details}}
                <li>{{.}}</li>
                {{/Transaction.Details}}
            </ul>
        </div>

        <div class=""footer"">
            <p>Thank you for using Adyen!</p>
        </div>
    </div>
</body>
</html>
";

            var receiptModel = new AdyenReceiptModel
            {
                Merchant = new MerchantInfo
                {
                    Name = "Example Store",
                    ID = "MERCHANT12345"
                },
                Transaction = new TransactionInfo
                {
                    ID = "TXN7890",
                    Date = "2024-12-17",
                    Amount = new AmountInfo
                    {
                        Currency = "USD",
                        Value = 49.99m
                    },
                    Status = "Completed",
                    Details = new System.Collections.Generic.List<string>
                    {
                        "Payment method: Credit Card",
                        "Card Type: Visa",
                        "Authorization Code: AUTH4567"
                    }
                },
                Voucher = new VoucherInfo
                {
                    Code = "ABCDEF123456",
                    ExpiryDate = "2025-01-31"
                }
            };

            var renderer = new MustacheRenderer();

            // Act
            string renderedHtml = renderer.Render(template, receiptModel);

            // Output the result to Debug (for demonstration purposes)
            Debug.WriteLine(renderedHtml);

            // Assert
            Assert.IsNotNull(renderedHtml);
            Assert.IsTrue(renderedHtml.Contains("Example Store"));
            Assert.IsTrue(renderedHtml.Contains("MERCHANT12345"));
            Assert.IsTrue(renderedHtml.Contains("TXN7890"));
            Assert.IsTrue(renderedHtml.Contains("USD 49.99"));
            Assert.IsTrue(renderedHtml.Contains("Completed"));
            Assert.IsTrue(renderedHtml.Contains("ABCDEF123456"));
            Assert.IsTrue(renderedHtml.Contains("2025-01-31"));
            Assert.IsTrue(renderedHtml.Contains("Payment method: Credit Card"));
            Assert.IsTrue(renderedHtml.Contains("Card Type: Visa"));
            Assert.IsTrue(renderedHtml.Contains("Authorization Code: AUTH4567"));


        }

        [TestMethod]
        public void RenderAdyenVoucherReceipt_WithoutVoucher_ShouldRenderCorrectly()
        {
            // Arrange
            string template = @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <title>Adyen Receipt - Captive Portal Voucher</title>
    <style>
        body { font-family: Arial, sans-serif; }
        .receipt { max-width: 600px; margin: auto; border: 1px solid #ccc; padding: 20px; }
        .header { text-align: center; }
        .details, .voucher, .transaction { margin-top: 20px; }
        .details th, .details td { padding: 5px 10px; text-align: left; }
        .voucher-code { font-size: 1.2em; color: #2E8B57; }
    </style>
</head>
<body>
    <div class=""receipt"">
        <div class=""header"">
            <h1>Adyen Receipt</h1>
            <h2>Captive Portal Voucher</h2>
        </div>

        <div class=""details"">
            <table>
                <tr>
                    <th>Merchant:</th>
                    <td>{{Merchant.Name}}</td>
                </tr>
                <tr>
                    <th>Merchant ID:</th>
                    <td>{{Merchant.ID}}</td>
                </tr>
                <tr>
                    <th>Transaction ID:</th>
                    <td>{{Transaction.ID}}</td>
                </tr>
                <tr>
                    <th>Date:</th>
                    <td>{{Transaction.Date}}</td>
                </tr>
                <tr>
                    <th>Amount:</th>
                    <td>{{Transaction.Amount.Currency}} {{Transaction.Amount.Value}}</td>
                </tr>
                <tr>
                    <th>Status:</th>
                    <td>{{Transaction.Status}}</td>
                </tr>
            </table>
        </div>

        {{#Voucher}}
        <div class=""voucher"">
            <h3>Your Voucher</h3>
            <p>Use the following code to access the captive portal:</p>
            <p class=""voucher-code"">{{Code}}</p>
            <p>Valid until: {{ExpiryDate}}</p>
        </div>
        {{/Voucher}}

        <div class=""transaction"">
            <h3>Transaction Details</h3>
            <ul>
                {{#Transaction.Details}}
                <li>{{.}}</li>
                {{/Transaction.Details}}
            </ul>
        </div>

        <div class=""footer"">
            <p>Thank you for using Adyen!</p>
        </div>
    </div>
</body>
</html>
";

            var receiptModel = new AdyenReceiptModel
            {
                Merchant = new MerchantInfo
                {
                    Name = "Example Store",
                    ID = "MERCHANT12345"
                },
                Transaction = new TransactionInfo
                {
                    ID = "TXN7890",
                    Date = "2024-12-17",
                    Amount = new AmountInfo
                    {
                        Currency = "USD",
                        Value = 49.99m
                    },
                    Status = "Completed",
                    Details = new System.Collections.Generic.List<string>
                    {
                        "Payment method: Credit Card",
                        "Card Type: Visa",
                        "Authorization Code: AUTH4567"
                    }
                },
                Voucher = null // No voucher
            };

            var renderer = new MustacheRenderer();

            // Act
            string renderedHtml = renderer.Render(template, receiptModel);

            // Output the result to Debug (for demonstration purposes)
            Debug.WriteLine(renderedHtml);

            // Assert
            Assert.IsNotNull(renderedHtml);
            Assert.IsTrue(renderedHtml.Contains("Example Store"));
            Assert.IsTrue(renderedHtml.Contains("MERCHANT12345"));
            Assert.IsTrue(renderedHtml.Contains("TXN7890"));
            Assert.IsTrue(renderedHtml.Contains("USD 49.99"));
            Assert.IsTrue(renderedHtml.Contains("Completed"));
            Assert.IsFalse(renderedHtml.Contains("Your Voucher")); // Voucher section should not be rendered
            Assert.IsFalse(renderedHtml.Contains("ABCDEF123456"));
            Assert.IsFalse(renderedHtml.Contains("2025-01-31"));
            Assert.IsTrue(renderedHtml.Contains("Payment method: Credit Card"));
            Assert.IsTrue(renderedHtml.Contains("Card Type: Visa"));
            Assert.IsTrue(renderedHtml.Contains("Authorization Code: AUTH4567"));
        }
    }
}
