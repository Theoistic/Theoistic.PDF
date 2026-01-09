using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Theoistic.PDF;
using Theoistic.PDFWebTest.PDFViews;
using Theoistic.PDFWebTest.Views.PDFViews;

namespace Theoistic.PDFWebTest.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        public async Task OnGetAsync()
        {
            //Parallel.For(0, 1, async (x) =>
            //{
            //    var pdfConfig = new PDFBuilder()
            //        .Settings(x =>
            //        {
            //            x.UseCompression = true;
            //        })
            //        .InjectCSS("wwwroot/PDFStyle.css")
            //        .RazorView("PDFViews/NicePDF", new NicePDFModel
            //        {
            //            Items = new List<NicePDFModel.Item> {
            //            new NicePDFModel.Item { Name = "Something " + x, Value = "10.42" },
            //            new NicePDFModel.Item { Name = "Something else", Value = "50.42" },
            //            new NicePDFModel.Item { Name = "Something more", Value = "21.42" },
            //            }
            //        });

            //    var html = await pdfConfig.BuildHTMLAsync();

            //    var pdf = await pdfConfig.BuildAsync();

            //    System.IO.File.WriteAllBytes($"test{x}.pdf", pdf);
            //});
        }
    }
}