﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Mvc;
using Newtonsoft.Json;
using WaveAccountingIntegration.Models;


namespace WaveAccountingIntegration.Controllers
{
	public class BusinessProcessingMahayagController : BaseController
	{
		public ActionResult RefreshBankConnections()
		{
			List<Connected_Site> refreshedSites = new List<Connected_Site>();

			//refresh personal
			var connectedPersonalSites = _restService.Get<List<Connected_Site>>(
				$"https://integrations.waveapps.com/{_appAppSettings.PersonalGuid}/bank/connected-sites-widget", _headers).Result;
			foreach (var site in connectedPersonalSites)
			{
				var refreshResult = _restService.Post<string, object>(
					$"https://integrations.waveapps.com/{_appAppSettings.PersonalGuid}/bank/refresh-accounts/{site.id}", null, _headers);

				if (refreshResult.IsSuccessStatusCode)
				{
					refreshedSites.Add(site);
					Thread.Sleep(2500);
				}
			}


			var businesses = _restService.Get<List<Business>>("https://api.waveapps.com/businesses/");

			foreach (var business in businesses.Result)
			{
				var connectedSites = _restService.Get<List<Connected_Site>>(
					$"https://integrations.waveapps.com/{business.id}/bank/connected-sites-widget", _headers).Result;

				foreach (var site in connectedSites)
				{
					var refreshResult = _restService.Post<string, object>(
						$"https://integrations.waveapps.com/{business.id}/bank/refresh-accounts/{site.id}", null, _headers);

					if (refreshResult.IsSuccessStatusCode)
					{
						refreshedSites.Add(site);
						Thread.Sleep(2500);
					}
				}
			}

			ViewBag.Message = "RefreshBankConnections";
			return View(refreshedSites);
		}

		public ActionResult SetCustomerDefaults()
		{
			var processedCsutomers = new List<Customer>();

			var allCustomers = _restService.Get<List<Customer>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/").Result;

			var activeCustomersToSetup = allCustomers.Where(x => x.active && !x.name.StartsWith("XX"));

			foreach (var customer in activeCustomersToSetup)
			{
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(customer);

				if (custSettings == null)
				{
					var defaultCustSettings = new CustomerSettings()
					{
						ChargeLateFee = false,
						ConsolidateInvoices = true,
						NextLateFeeChargeDate = DateTime.Today
					};

					customer.shipping_details = new Shipping_details()
					{
						delivery_instructions = JsonConvert.SerializeObject(defaultCustSettings),

						ship_to_contact = $"{customer.name}",
						phone_number = String.Empty,
						address1 = String.Empty,
						address2 = String.Empty,
						city = String.Empty,
						postal_code = String.Empty,
					};

					var updatedCustomerResult =
						_restService.Patch<UpdateCustomerResult, Customer>(customer.url, customer);

					if (updatedCustomerResult.IsSuccessStatusCode == false)
					{
						throw new InvalidOperationException("SetCustomerDefaults failed");
					}

					processedCsutomers.Add(customer);

				}
			}

			ViewBag.Message = "SetCustomerDefaults";
			return View(processedCsutomers);
		}

		public ActionResult ConsolidateInvoices(int narrrowByCustomerId = 0)
		{
			var processedInvoices = new List<Invoice>();

			var url =
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/?embed_customer=true";

			if (narrrowByCustomerId != 0)
				url = url + $"&customer.id={narrrowByCustomerId}";

			var invoicesDue = _restService.Get<List<Invoice>>(url).Result
				.Where(x=> x.invoice_amount_due > 0)
				.OrderBy(x=>x.customer.name)
				.ToList();

			var distinctCustomerIds = invoicesDue.Select(x => x.customer.id).Distinct();

			foreach (var customerId in distinctCustomerIds)
			{
				var customerInvoicesDue = invoicesDue.Where(x => x.customer.id == customerId).OrderByDescending(x => x.invoice_date);
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(customerInvoicesDue.First().customer);

				//consolidate more than 2 invoices when settings allow it
				if (customerInvoicesDue.Count() > 1 && custSettings.ConsolidateInvoices == true)
				{
					var targetInvoice = customerInvoicesDue.Last();
					var sourceInvoices = customerInvoicesDue.Where(x => x.id != targetInvoice.id);

					foreach (var sourceInvoice in sourceInvoices)
					{
						//check for payments
						var payments = _restService.Get<List<Payment>>(sourceInvoice.payments_url).Result;

						//TODO: consolidate invoices with payments
						//process src invoice only if there are no payments
						if (payments.Count == 0)
						{
							#region transfer Items
							var sourceItems = _restService.Get<List<InvoiceItem>>(sourceInvoice.items_url).Result;

							foreach (var sourceItem in sourceItems)
							{
								var movedItem = new InvoiceItem
								{
									product = new Product { id = sourceItem.product.id },
									description = $"Transfered item: [{sourceItem.description}] " +
									              $"from invoice: {sourceInvoice.invoice_number} " +
									              $"for period from: {sourceInvoice.invoice_date.ToShortDateString()} to: {sourceInvoice.due_date.ToShortDateString()} " +
									              $"transfered on: {DateTime.Now.ToShortDateString()}",
									quantity = sourceItem.quantity,
									price = sourceItem.price
								};

								var addResult = _restService.Post<AddInvoiceItemResponse, InvoiceItem>(targetInvoice.items_url, movedItem);

								if (addResult.IsSuccessStatusCode)
								{
									
									#region zero out source item price


									var zeroSourceInvoiceItemResult = _restService.Patch<UpdateInvoiceItemResponse, InvoiceItem>(sourceItem.url, new InvoiceItem()
									{
										product = new Product() { id = sourceItem.product.id },
										description = $"[{sourceItem.description}] " +
										              $"price: {movedItem.price} " +
										              $"was moved to invoice: {targetInvoice.invoice_number} " +
										              $"on: {DateTime.Now.ToShortDateString()}",
										quantity = sourceItem.quantity,
										price = 0,
									});

									if (zeroSourceInvoiceItemResult.IsSuccessStatusCode == false)
									{
										throw new InvalidOperationException("Saving zeroSourceInvoiceItem failed");
									}

									#endregion
								}
								else
								{
									throw new InvalidOperationException("add movedItem failed");
								}
							}
							#endregion

							processedInvoices.Add(sourceInvoice);
						}
					}
				}
				
			}
			ViewBag.Message = "ConsolidateInvoices";
			return View(processedInvoices);
		}

		public ActionResult DisableInvoicePayments()
		{
			var invoices = _restService.Get<List<Invoice>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/");

			List<Invoice> updatedInvoices = new List<Invoice>();

			if (invoices.IsSuccessStatusCode)
			{
				var invoicesToProcess = invoices.Result.Where(
					x => x.invoice_amount_due > 0 && 
					(x.disable_bank_payments == false || x.disable_credit_card_payments == false)
				);

				foreach (var invoice in invoicesToProcess)
				{
					var disablePaymentInvoice = new InvoiceDisablePayments
					{
						disable_credit_card_payments = true,
						disable_bank_payments = true
					};

					var updatedInvoiceResult = _restService.Patch<Invoice, InvoiceDisablePayments>(invoice.url, disablePaymentInvoice);
					if (updatedInvoiceResult.IsSuccessStatusCode)
					{
						updatedInvoices.Add(updatedInvoiceResult.Result);
					}
					else
					{
						throw new InvalidOperationException("Failed to save disable_payments = true");
					}
				}
			}
			else
			{
				throw new InvalidOperationException("Failed to retrieve invoice list");
			}

			ViewBag.Message = "DisableInvoicePayments";
			return View(updatedInvoices);
		}

		public ActionResult ChargeLateFees(double latePercentRate = 0.02)
		{
			var catchupOnLatefees = false;
			List<Invoice> addedFeeInvoices = new List<Invoice>();

			var overdueInvoices = _restService.Get<List<Invoice>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/?status=overdue&embed_customer=true&customer.id=14361244");
			

			//TODO: handle multiple invoices per single customer
			foreach (var invoice in overdueInvoices.Result)
			{
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(invoice.customer);

				if (custSettings?.ChargeLateFee != null && custSettings.ChargeLateFee.Value && 
					custSettings.NextLateFeeChargeDate != null && custSettings.NextLateFeeChargeDate.Value.Date <= DateTime.Now.Date)
				{
					#region add late fee
					var lateFee = new InvoiceItem
					{
						product = new Product{ id = _appAppSettings.MahayagLateFeeProductId },
						description = $"Late Charge: {100 * latePercentRate}% " +
						              $"from PastDueAmount: {invoice.invoice_amount_due} " +
						              $"as of date: {custSettings.NextLateFeeChargeDate.Value.Date.ToShortDateString()} " +
						              $"added on: {DateTime.Now.ToShortDateString()}",
						quantity = 1,
						price = invoice.invoice_amount_due * latePercentRate,
					};

					var addResult = _restService.Post<AddInvoiceItemResponse, InvoiceItem>(invoice.items_url, lateFee);

					if (addResult.IsSuccessStatusCode)
					{
						addedFeeInvoices.Add(invoice);

						#region update NextLateFeeChargeDate

						custSettings.NextLateFeeChargeDate = custSettings.NextLateFeeChargeDate.Value.AddDays(1).Date;

						var updatedCustomerResult =
							_customerSettingsService.SaveUpdatedCustomerSettings(invoice.customer.url, custSettings, _restService);

						if (updatedCustomerResult.IsSuccessStatusCode == false)
						{
							throw new InvalidOperationException("Saving NextLateFeeChargeDate failed");
						}

						#endregion
					}
					else
					{
						throw new InvalidOperationException("add lateFee failed");
					}
					#endregion
					
					//determine if process needs to catch up on late fees
					if (custSettings.NextLateFeeChargeDate.Value <= DateTime.Today)
						catchupOnLatefees = true;
				}
			}

			if (catchupOnLatefees)
			{
				//recusrsive call to catch up on late fees if process has not run for more than a day
				ChargeLateFees(latePercentRate);
			}

			return View(addedFeeInvoices);
		}

		private class AddInvoiceItemResponse
		{
		}

		private class UpdateInvoiceItemResponse
		{
		}

		private class UpdateCustomerResult
		{
		}
	}
}
