using Hangfire;
using CompanyName.Models.Domain;
using CompanyName.Models.Interfaces;
using CompanyName.Models.Requests;
using CompanyName.Models.ViewModels;
using CompanyName.Services;
using CompanyName.Services.Interfaces;
using CompanyName.Services.Tools;
using Stripe;
using System;
using System.Collections.Generic;
using System.Web.Mvc;
using System.Web.Routing;

namespace CompanyName.Web.Controllers
{
    [HandleError]
    public class MemberController : BaseController
    {
        IUserService _userService;
        IAppTokenService _appTokenService;

        public MemberController(IUserService userService, IAppTokenService appTokenService)
        {
            _userService = userService;
            _appTokenService = appTokenService;
            StripeConfiguration.SetApiKey(AppConfiguration.Instance.AppGetByKey("StripeSecretKey"));
        }

        //CHECKS TO SEE IF USER IS VALID AND ALSO CHECKS TO SEE THE STATUS OF USER'S OR
        //INVITER'S SUBSCRIPTION STATUS
        private ActionResult CheckStatus()
        {
            int id = _userService.GetCurrentUserId();
            //IF USER DOES NOT EXIST, SEND THEM TO HOME PAGE, BECAUSE THIS IS
            //WHERE THE REQUEST INVITE IS.
            if (id <= 0)
            {
                Response.Redirect("/");
                return null;
            //OTHERWISE IF THEY ARE A VALID USER, CHECK THEIR SUBSCRIPTION STATUS
            }
            else
            {
                return CheckSubscription();
            }
        }

        //CHECKS IF CURRENT USER HAS A VALID SUBSCRIPTION
        private ActionResult CheckSubscription()
        {
            try
            {
                int userBaseId = _userService.GetCurrentUserId();
                SubscriptionStatusRequest subStatus = new SubscriptionStatusRequest();
                subStatus = _userService.GetSubStatus(userBaseId);
                //THIS CASE IS FOR IF YOU'VE BEEN INVITED BY AN ADMIN TO BE A SUBSCRIBER
                //YOU WILL BE REDIRECTED TO THE SUBSCRIPTION SIGN UP PAGE UPON REGISTRATION COMPLETION
                if (subStatus.SubExists == false && subStatus.SubValid == false)
                {
                    return RedirectToAction("Subscriptions");
                }
                //THIS CASE IS FOR IF YOUR SUBSCRIPTION EXISTS BUT HAS ENDED. WILL AUTOMATICALLY RENEW VIA
                //HANGFIRE/STRIPE AND THEN WILL RETURN AN EMPTY VIEW FOR YOU TO CONTINUE WITH WHATEVER
                //PAGE YOU WERE GOING TO
                else if (subStatus.SubExists == true && subStatus.SubValid == false)
                {
                    RenewSubscriptionById(userBaseId);
                    return View();
                }
                //THIS CASE IS FOR IF YOUR SUBSCRIPTION EXISTS AND IS VALID
                return View();
            }
            //SET UP AN ERROR IN SQL. IF ERROR IS THROWN, WILL CATCH IT HERE AND PASS
            //ERROR MESSAGE TO HOME/INDEX PAGE WHERE SWEET ALERT WILL APPEAR TO TELL USER
            //THAT THEIR SUBSCRIBER/INVITER'S SUBSCRIPTION HAS ENDED. SENT TO HOME PAGE SO THEY
            //CAN CLICK ON REQUEST INVITE BUTTON.
            catch (Exception ex)
            {
                //TEMP DATA IS A ONE-TIME WAY TO SEND DATA TO ANOTHER CONTROLLER OR TO A VIEW
                TempData["Message"] = ex.Message;
                //USING REDIRECTTOACTION TO REDIRECT CONTROL TO CONTROLLER AND ACTION STATED BELOW
                return RedirectToAction("Index", new RouteValueDictionary(new { controller = "Home", action = "Index" }));
            }
        }

        //CHECKS IF ANY USERS HAVE EXPIRED SUBSCRIPTION ON A DAILY BASIS, VIA HANGFIRE(BACKGROUND JOB INITIATOR/MANAGER)
        private void SubscriptionValidCheck()
        {
            RecurringJob.AddOrUpdate("Subscription Renewal", () => RenewSubscriptions(), Cron.Daily);
        }

        //IF THE DAILY SUBSCRIPTION RENEWAL DOESN'T CATCH YOUR EXPIRED SUBSCRIPTION, THIS ONE WILL
        //WILL CHARGE AND RENEW CURRENT LOGGED IN USER'S EXPIRED SUBSCRIPTION UPON REACHING ANY MEMBER PAGE
        private void RenewSubscriptionById(int userBaseId)
        {
            StripePayment cust = _userService.GetExpiredSubscriptionById(userBaseId);
            StripePaymentRequest model = new StripePaymentRequest();
            model.AmountInPennies = cust.AmountInPennies;
            model.Customer = cust.Customer;
            model.Email = cust.Email;
            model.UserBaseId = cust.UserBaseId;
            Charge(model);
        }

        //MESSAGE CALLED ON BY HANGFIRE THAT WILL PULL ALL EXPIRED SUBSCRIPTIONS AND WILL
        //GO THROUGH EACH OF THOSE EXPIRED SUBSCRIPTIONS, CHARGE THEM, AND THEN RENEW THEM
        public void RenewSubscriptions()
        {
            List<StripePayment> expiredSubList = _userService.GetExpiredSubscriptions();
            StripePaymentRequest model = new StripePaymentRequest();
            foreach (StripePayment cust in expiredSubList)
            {

                model.AmountInPennies = cust.AmountInPennies;
                model.Customer = cust.Customer;
                model.Email = cust.Email;
                model.UserBaseId = cust.UserBaseId;
                Charge(model);
            }
        }

        //USED TO CHECK STATUS OF USER BEFORE RETURNING VIEW ON ANY ACTIONS THAT REQUIRE SOME SORT
        //OF BASEVIEWMODEL
        private ActionResult CheckStatusAndFilterActionResult(BaseViewModel bvm)
        {
            var temp = CheckStatus();
            //IF NULL, THEN NOT A USER OF CompanyName, SO TAKE THEM BACK TO HOME PAGE VIEW
            if (temp == null)
            {
                return null;
            }
            //IF VIEWRESULT THEN TAKE USER TO THE PAGE THEY WERE TRYING TO GO TO
            else if (temp.GetType() == typeof(ViewResult) && temp != null)
            {
                return View(bvm);
            }
            //IF REDIRECTTOROUTERESULT THEN REDIRECTS YOU TO THAT PAGE
            else if (temp.GetType() == typeof(RedirectToRouteResult) && temp != null)
            {
                return temp;
            }
            //ELSE, RETURN NULL. DIDN'T WANT TO RETURN VIEW BECAUSE DIDN'T WANT TO GIVE CHANCE OF
            //RETURNING VIEW OF THIS ACTION
            else
            {
                return null;
            }
        }

        [Route("Member/Subscriptions")]
        public ActionResult Subscriptions()
        {
            return View();
        }

        //CREATES A CHARGE FOR A CUSTOMER: CREATES A CUSTOMER ID AND TOKEN IF YOU'RE NEW
        //IF YOU'RE NOT NEW, WILL GRAB YOUR CUSTOMER TOKEN FROM DATABASE AND WILL USE THAT 
        //TOKEN TO TELL STRIPE TO CHARGE YOUR CARD. WE ALSO KEEP TRANSACTION TOKENS GENERATED 
        //FROM EVERY CHARGE.
        [Route("Member/Subscription/Charge"), HttpPost]
        public JsonResult Charge(StripePaymentRequest model)
        {
            if (model.UserBaseId == 0)
            {
                model.UserBaseId = _userService.GetCurrentUserId();
            }
            //GET STRIPE SECRET KEY
            //INSTANTIATED ABOVE IN CONSTRUCTOR

            //IF CUSTOMER ID IS NOT PRESENT, CREATE NEW CUSTOMER, AND CHARGE CUSTOMER'S CARD
            if (model.Customer == null)
            {
                var customers = new StripeCustomerService();
                var customer = customers.Create(new StripeCustomerCreateOptions
                {
                    Email = model.Email,
                    SourceToken = model.Id
                });

                //CHARGE THE USER'S CARD
                var charges = new StripeChargeService();
                var charge = charges.Create(new StripeChargeCreateOptions
                {
                    Amount = model.AmountInPennies,
                    Currency = "USD",
                    Description = "Subscription Charge",
                    CustomerId = customer.Id
                });

                //RETURN JSON OBJECT TO PROMISE IN STRIPE.JS DIRECTIVE. TOKEN WILL BE STORED
                //IN CALL IN STRIPE.JS. WE ARE DOING IT IN FRONT END BECAUSE WE NEED TO GET USER
                //INFORMATION
                return Json(charge);
            }
            //IF CUSTOMER ID IS PRESENT, USE IT TO CHARGE THE TRANSACTION.
            else
            {
                AppTokenAddUpdateRequest tknModel = new AppTokenAddUpdateRequest();
                tknModel.UserBaseId = model.UserBaseId;
                //HARD CODED APPTOKENTYPEID FOR STRIPE TRANSACTION TOKEN
                tknModel.AppTokenTypeId = 2;
                var charges = new StripeChargeService();
                var charge = charges.Create(new StripeChargeCreateOptions
                {
                    Amount = model.AmountInPennies,
                    Currency = "USD",
                    Description = "Subscription Charge",
                    CustomerId = model.Customer
                });
                tknModel.Token = charge.BalanceTransactionId;
                //ADD TRANSACTION TOKEN AND RENEW SUBSCRIPTION.
                _appTokenService.InsertStripeTxnId(tknModel);

                //RETURN JSON OBJECT TO PROMISE IN STRIPE.JS DIRECTIVE
                return Json(charge);
            }
        }

        //GETS USER'S STRIPE TRANSACTION HISTORY, AND ALLOWS FOR PAGINATION WHEN RECORDS ARE MORE THAN 100
        [Route("Member/SubscriptionTransactionHistoryQuery/{inputDirection}/{trxId}"), HttpGet]
        public JsonResult SubscriptionTransactionHistoryQuery(string inputDirection = null, string trxId = null)
        {
            //GET STRIPE CUSTOMER ID/TOKEN
            StripeIdRequest customerId = _userService.GetStripeCustomerId(_userService.GetCurrentUserId());

            //IF WE'VE CHARGED THIS CUSTOMER BEFORE, THEN SEND A REQUEST TO STRIPE FOR USER'S TRANSACTION
            //HISTORY. WE ALSO HAVE PAGINATION OPTIONS TOWARDS THE END OF THIS CONDITIONAL. THE WAY STRIPE'S PAGINATION
            //WORKS IS THAT THEY ALLOW YOU TO GET DATA AFTER A CERTAIN ID, OR BEFORE. SO WHEN WE PERFORM THIS REQUEST
            //WE ARE PASSING IN A TRANSACTION ID AND AN INPUT DIRECTION.
            if (customerId.Token != null)
            {
                var chargeService = new StripeChargeService();

                StripeChargeListOptions opts = new StripeChargeListOptions
                {
                    Limit = 100,
                    CustomerId = customerId.Token
                };

                if (!string.IsNullOrEmpty(trxId) && inputDirection == "next")
                    opts.StartingAfter = trxId; //LAST TRANSACTION NUMBER FROM SET

                if (!string.IsNullOrEmpty(trxId) && inputDirection == "prev")
                    opts.EndingBefore = trxId; //FIRST TRANSACTION NUMBER FROM SET

                StripeList<StripeCharge> chargeItems = chargeService.List(opts);
                return Json(chargeItems, JsonRequestBehavior.AllowGet);
            }
            //IF WE'VE NEVER CHARGED THE CURRENT USER BEFORE, YOU WOULDN'T HAVE ANY HISTORY, SO RETURN ERROR
            else
            {
                return Json(new { message = "You have no transaction history!" }, JsonRequestBehavior.AllowGet);
            }
        }
    }
}