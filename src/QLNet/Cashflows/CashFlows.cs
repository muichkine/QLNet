/*
 Copyright (C) 2008, 2009 Siarhei Novik (snovik@gmail.com)
 Copyright (C) 2008-2016 Andrea Maggiulli (a.maggiulli@gmail.com)

 This file is part of QLNet Project https://github.com/amaggiulli/qlnet

 QLNet is free software: you can redistribute it and/or modify it
 under the terms of the QLNet license.  You should have received a
 copy of the license along with this program; if not, license is
 available at <https://github.com/amaggiulli/QLNet/blob/develop/LICENSE>.

 QLNet is a based on QuantLib, a free-software/open-source library
 for financial quantitative analysts and developers - http://quantlib.org/
 The QuantLib license is available online at http://quantlib.org/license.shtml.

 This program is distributed in the hope that it will be useful, but WITHOUT
 ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 FOR A PARTICULAR PURPOSE.  See the license for more details.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QLNet
{
   //! %cashflow-analysis functions
   public class CashFlows
   {      
      #region utility functions

      // helper function used to calculate Time-To-Discount for each stage when calculating discount factor stepwisely
      public static double getStepwiseDiscountTime(CashFlow cashFlow, DayCounter dc, Date npvDate, Date lastDate)
      {
         Date cashFlowDate = cashFlow.date();
         Date refStartDate, refEndDate;
         Coupon coupon = cashFlow as Coupon;
         if (coupon != null)
         {
            refStartDate = coupon.referencePeriodStart;
            refEndDate = coupon.referencePeriodEnd;
         }
         else
         {
            if (lastDate == npvDate)
            {
               // we don't have a previous coupon date,
               // so we fake it
               refStartDate = cashFlowDate - new Period(1, TimeUnit.Years);
            }
            else
            {
               refStartDate = lastDate;
            }

            refEndDate = cashFlowDate;

         }

         if (coupon != null && lastDate != coupon.accrualStartDate())
         {
            double couponPeriod = dc.yearFraction(coupon.accrualStartDate(), cashFlowDate, refStartDate, refEndDate);
            double accruedPeriod = dc.yearFraction(coupon.accrualStartDate(), lastDate, refStartDate, refEndDate);
            return couponPeriod - accruedPeriod;
         }
         else
         {
            return dc.yearFraction(lastDate, cashFlowDate, refStartDate, refEndDate);
         }
      }


      #endregion

      #region Helper Classes

      class IrrFinder : ISolver1d
      {
         private Leg leg_;
         private double npv_;
         private DayCounter dayCounter_;
         private Compounding compounding_;
         private Frequency frequency_;
         private bool includeSettlementDateFlows_;
         private Date settlementDate_, npvDate_;

         public IrrFinder(Leg leg, double npv, DayCounter dayCounter, Compounding comp, Frequency freq,
                          bool includeSettlementDateFlows, Date settlementDate, Date npvDate)
         {
            leg_ = leg;
            npv_ = npv;
            dayCounter_ = dayCounter;
            compounding_ = comp;
            frequency_ = freq;
            includeSettlementDateFlows_ = includeSettlementDateFlows;
            settlementDate_ = settlementDate;
            npvDate_ = npvDate;

            if (settlementDate == null)
               settlementDate_ = Settings.evaluationDate();

            if (npvDate == null)
               npvDate_ = settlementDate_;

            checkSign();
         }

         public override double value(double y)
         {
            InterestRate yield = new InterestRate(y, dayCounter_, compounding_, frequency_);
            double NPV = CashFlows.npv(leg_, yield, includeSettlementDateFlows_, settlementDate_, npvDate_);
            return npv_ - NPV;
         }

         public override double derivative(double y)
         {
            InterestRate yield = new InterestRate(y, dayCounter_, compounding_, frequency_);
            return leg_.modifiedDuration(yield, includeSettlementDateFlows_, settlementDate_, npvDate_);
         }

         private void checkSign()
         {
            // depending on the sign of the market price, check that cash
            // flows of the opposite sign have been specified (otherwise
            // IRR is nonsensical.)

            int lastSign = Math.Sign(-npv_), signChanges = 0;
            for (int i = 0; i < leg_.Count; ++i)
            {
               if (!leg_[i].hasOccurred(settlementDate_, includeSettlementDateFlows_) &&
                   !leg_[i].tradingExCoupon(settlementDate_))
               {
                  int thisSign = Math.Sign(leg_[i].amount());
                  if (lastSign * thisSign < 0) // sign change
                     signChanges++;

                  if (thisSign != 0)
                     lastSign = thisSign;
               }
            }
            Utils.QL_REQUIRE(signChanges > 0, () =>
                             "the given cash flows cannot result in the given market " +
                             "price due to their sign");
         }
      }
      class ZSpreadFinder : ISolver1d
      {
         private Leg leg_;
         private double npv_;
         private SimpleQuote zSpread_;
         ZeroSpreadedTermStructure curve_;
         private bool includeSettlementDateFlows_;
         private Date settlementDate_, npvDate_;

         public ZSpreadFinder(Leg leg, YieldTermStructure discountCurve, double npv, DayCounter dc, Compounding comp, Frequency freq,
                              bool includeSettlementDateFlows, Date settlementDate, Date npvDate)
         {
            leg_ = leg;
            npv_ = npv;
            zSpread_ = new SimpleQuote(0.0);
            curve_ = new ZeroSpreadedTermStructure(new Handle<YieldTermStructure>(discountCurve),
                                                   new Handle<Quote>(zSpread_), comp, freq, dc);
            includeSettlementDateFlows_ = includeSettlementDateFlows;
            settlementDate_ = settlementDate;
            npvDate_ = npvDate;

            if (settlementDate == null)
               settlementDate_ = Settings.evaluationDate();

            if (npvDate == null)
               npvDate_ = settlementDate_;

            // if the discount curve allows extrapolation, let's
            // the spreaded curve do too.
            curve_.enableExtrapolation(discountCurve.allowsExtrapolation());
         }

         public override double value(double zSpread)
         {
            zSpread_.setValue(zSpread);
            double NPV = CashFlows.npv(leg_, curve_, includeSettlementDateFlows_, settlementDate_, npvDate_);
            return npv_ - NPV;
         }


      }
      class BPSCalculator : IAcyclicVisitor
      {
         private YieldTermStructure discountCurve_;
         double bps_, nonSensNPV_;

         public BPSCalculator(YieldTermStructure discountCurve)
         {
            discountCurve_ = discountCurve;
            nonSensNPV_ = 0.0;
            bps_ = 0.0;
         }

         #region IAcyclicVisitor pattern
         // visitor classes should implement the generic visit method in the following form
         public void visit(object o)
         {
            Type[] types = new Type[] { o.GetType() };
            MethodInfo methodInfo = Utils.GetMethodInfo(this, "visit", types);

            if (methodInfo != null)
            {
               methodInfo.Invoke(this, new object[] { o });
            }
         }
         public void visit(Coupon c)
         {
            double bps = c.nominal() *
                         c.accrualPeriod() *
                         discountCurve_.discount(c.date());
            bps_ += bps;
         }
         public void visit(CashFlow cf)
         {
            nonSensNPV_ += cf.amount() * discountCurve_.discount(cf.date());
         }
         #endregion

         public double bps() { return bps_; }
         public double nonSensNPV() { return nonSensNPV_; }
      }
      #endregion

      #region YieldTermStructure functions

      //! NPV of the cash flows. The NPV is the sum of the cash flows, each discounted according to the given term structure.
      public static double npv(Leg leg, YieldTermStructure discountCurve, bool includeSettlementDateFlows,
                               Date settlementDate = null, Date npvDate = null)
      {

         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         double totalNPV = 0.0;
         for (int i = 0; i < leg.Count; ++i)
         {
            if (!leg[i].hasOccurred(settlementDate, includeSettlementDateFlows) && !leg[i].tradingExCoupon(settlementDate))
               totalNPV += leg[i].amount() * discountCurve.discount(leg[i].date());
         }

         return totalNPV / discountCurve.discount(npvDate);
      }

      // Basis-point sensitivity of the cash flows.
      // The result is the change in NPV due to a uniform 1-basis-point change in the rate paid by the cash flows. The change for each coupon is discounted according to the given term structure.
      public static double bps(Leg leg, YieldTermStructure discountCurve, bool includeSettlementDateFlows,
                               Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         BPSCalculator calc = new BPSCalculator(discountCurve);
         for (int i = 0; i < leg.Count; ++i)
         {
            if (!leg[i].hasOccurred(settlementDate, includeSettlementDateFlows) &&
                !leg[i].tradingExCoupon(settlementDate))
               leg[i].accept(calc);
         }
         return Constants.BasisPoint * calc.bps() / discountCurve.discount(npvDate);
      }
      //! NPV and BPS of the cash flows.
      // The NPV and BPS of the cash flows calculated together for performance reason
      public static void npvbps(Leg leg, YieldTermStructure discountCurve, bool includeSettlementDateFlows,
                                Date settlementDate, Date npvDate, out double npv, out double bps)
      {
         npv = bps = 0.0;
         if (leg.empty())
         {
            bps = 0.0;
            return;
         }

         for (int i = 0; i < leg.Count; ++i)
         {
            CashFlow cf = leg[i];
            if (!cf.hasOccurred(settlementDate, includeSettlementDateFlows) &&
                !cf.tradingExCoupon(settlementDate))
            {
               Coupon cp = leg[i] as Coupon;
               double df = discountCurve.discount(cf.date());
               npv += cf.amount() * df;
               if (cp != null)
                  bps += cp.nominal() * cp.accrualPeriod() * df;
            }
         }
         double d = discountCurve.discount(npvDate);
         npv /= d;
         bps = Constants.BasisPoint * bps / d;
      }

      // At-the-money rate of the cash flows.
      // The result is the fixed rate for which a fixed rate cash flow  vector, equivalent to the input vector, has the required NPV according to the given term structure. If the required NPV is
      //  not given, the input cash flow vector's NPV is used instead.
      public static double atmRate(Leg leg, YieldTermStructure discountCurve, bool includeSettlementDateFlows,
                                   Date settlementDate = null, Date npvDate = null, double? targetNpv = null)
      {

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         double npv = 0.0;
         BPSCalculator calc = new BPSCalculator(discountCurve);
         for (int i = 0; i < leg.Count; ++i)
         {
            CashFlow cf = leg[i];
            if (!cf.hasOccurred(settlementDate, includeSettlementDateFlows) &&
                !cf.tradingExCoupon(settlementDate))
            {
               npv += cf.amount() * discountCurve.discount(cf.date());
               cf.accept(calc);
            }
         }

         if (targetNpv == null)
            targetNpv = npv - calc.nonSensNPV();
         else
         {
            targetNpv *= discountCurve.discount(npvDate);
            targetNpv -= calc.nonSensNPV();
         }

         if (targetNpv.IsEqual(0.0))
            return 0.0;

         double bps = calc.bps();
         Utils.QL_REQUIRE(bps.IsNotEqual(0.0), () => "null bps: impossible atm rate");

         return targetNpv.Value / bps;
      }

      // NPV of the cash flows.
      // The NPV is the sum of the cash flows, each discounted
      // according to the given constant interest rate.  The result
      // is affected by the choice of the interest-rate compounding
      // and the relative frequency and day counter.
      public static double npv(Leg leg, InterestRate yield, bool includeSettlementDateFlows,
                               Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         double npv = 0.0;
         double discount = 1.0;
         Date lastDate = npvDate;
         DayCounter dc = yield.dayCounter();

         for (int i = 0; i < leg.Count; ++i)
         {
            if (leg[i].hasOccurred(settlementDate, includeSettlementDateFlows))
               continue;

            double amount = leg[i].amount();
            if (leg[i].tradingExCoupon(settlementDate))
            {
               amount = 0.0;
            }

            double b = yield.discountFactor(getStepwiseDiscountTime(leg[i], dc, npvDate, lastDate));
            discount *= b;
            lastDate = leg[i].date();

            npv += amount * discount;
         }
         return npv;
      }
      public static double npv(Leg leg, double yield, DayCounter dayCounter, Compounding compounding, Frequency frequency,
                               bool includeSettlementDateFlows, Date settlementDate = null, Date npvDate = null)
      {
         return npv(leg, new InterestRate(yield, dayCounter, compounding, frequency),
                    includeSettlementDateFlows, settlementDate, npvDate);
      }

      //! Basis-point sensitivity of the cash flows.
      // The result is the change in NPV due to a uniform
      // 1-basis-point change in the rate paid by the cash
      // flows. The change for each coupon is discounted according
      // to the given constant interest rate.  The result is
      // affected by the choice of the interest-rate compounding
      // and the relative frequency and day counter.

      public static double bps(Leg leg, InterestRate yield, bool includeSettlementDateFlows,
                               Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         FlatForward flatRate = new FlatForward(settlementDate, yield.rate(), yield.dayCounter(),
                                                yield.compounding(), yield.frequency());
         return bps(leg, flatRate, includeSettlementDateFlows, settlementDate, npvDate);
      }

      public static double bps(Leg leg, double yield, DayCounter dayCounter, Compounding compounding, Frequency frequency,
                               bool includeSettlementDateFlows, Date settlementDate = null, Date npvDate = null)
      {
         return bps(leg, new InterestRate(yield, dayCounter, compounding, frequency),
                    includeSettlementDateFlows, settlementDate, npvDate);
      }

      //! NPV of a single cash flows
      public static double npv(CashFlow cashflow, YieldTermStructure discountCurve,
                               Date settlementDate = null, Date npvDate = null, int exDividendDays = 0)
      {
         double NPV = 0.0;

         if (cashflow == null)
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         if (!cashflow.hasOccurred(settlementDate + exDividendDays))
            NPV = cashflow.amount() * discountCurve.discount(cashflow.date());


         return NPV / discountCurve.discount(npvDate);
      }


      //! CASH of the cash flows. The CASH is the sum of the cash flows.
      public static double cash(Leg cashflows, Date settlementDate = null, int exDividendDays = 0)
      {
         if (cashflows.Count == 0)
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         double totalCASH = cashflows.Where(x => !x.hasOccurred(settlementDate + exDividendDays)).
                            Sum(c => c.amount());

         return totalCASH;
      }

      //! Cash-flow convexity
      public static double convexity(Leg leg, InterestRate yield, bool includeSettlementDateFlows,
                                     Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         DayCounter dc = yield.dayCounter();

         double P = 0.0;
         double t = 0.0;
         double d2Pdy2 = 0.0;
         double r = yield.rate();
         int N = (int)yield.frequency();
         Date lastDate = npvDate;


         for (int i = 0; i < leg.Count; ++i)
         {
            if (leg[i].hasOccurred(settlementDate, includeSettlementDateFlows))
               continue;

            double c = leg[i].amount();
            if (leg[i].tradingExCoupon(settlementDate))
            {
               c = 0.0;
            }

            t += getStepwiseDiscountTime(leg[i], dc, npvDate, lastDate);

            double B = yield.discountFactor(t);
            P += c * B;
            switch (yield.compounding())
            {
               case  Compounding.Simple:
                  d2Pdy2 += c * 2.0 * B * B * B * t * t;
                  break;
               case Compounding.Compounded:
                  d2Pdy2 += c * B * t * (N * t + 1) / (N * (1 + r / N) * (1 + r / N));
                  break;
               case Compounding.Continuous:
                  d2Pdy2 += c * B * t * t;
                  break;
               case Compounding.SimpleThenCompounded:
                  if (t <= 1.0 / N)
                     d2Pdy2 += c * 2.0 * B * B * B * t * t;
                  else
                     d2Pdy2 += c * B * t * (N * t + 1) / (N * (1 + r / N) * (1 + r / N));
                  break;
               default:
                  Utils.QL_FAIL("unknown compounding convention (" + yield.compounding() + ")");
                  break;
            }
            lastDate = leg[i].date();
         }

         if (P.IsEqual(0.0))
            // no cashflows
            return 0.0;

         return d2Pdy2 / P;
      }

      public static double convexity(Leg leg, double yield, DayCounter dayCounter, Compounding compounding, Frequency frequency,
                                     bool includeSettlementDateFlows, Date settlementDate = null, Date npvDate = null)
      {
         return convexity(leg, new InterestRate(yield, dayCounter, compounding, frequency),
                          includeSettlementDateFlows, settlementDate, npvDate);
      }

      //! Basis-point value
      /*! Obtained by setting dy = 0.0001 in the 2nd-order Taylor
          series expansion.
      */
      public static double basisPointValue(Leg leg, InterestRate yield, bool includeSettlementDateFlows,
                                           Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         double npv = CashFlows.npv(leg, yield, includeSettlementDateFlows, settlementDate, npvDate);
         double modifiedDuration = CashFlows.duration(leg, yield, Duration.Type.Modified, includeSettlementDateFlows,
                                                      settlementDate, npvDate);
         double convexity = CashFlows.convexity(leg, yield, includeSettlementDateFlows, settlementDate, npvDate);
         double delta = -modifiedDuration * npv;
         double gamma = (convexity / 100.0) * npv;

         double shift = 0.0001;
         delta *= shift;
         gamma *= shift * shift;

         return delta + 0.5 * gamma;
      }
      public static double basisPointValue(Leg leg, double yield, DayCounter dayCounter, Compounding compounding, Frequency frequency,
                                           bool includeSettlementDateFlows, Date settlementDate = null, Date npvDate = null)
      {
         return basisPointValue(leg, new InterestRate(yield, dayCounter, compounding, frequency),
                                includeSettlementDateFlows, settlementDate, npvDate);
      }

      //! Yield value of a basis point
      /*! The yield value of a one basis point change in price is
          the derivative of the yield with respect to the price
          multiplied by 0.01
      */
      public static double yieldValueBasisPoint(Leg leg, InterestRate yield, bool includeSettlementDateFlows,
                                                Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         double npv = CashFlows.npv(leg, yield, includeSettlementDateFlows, settlementDate, npvDate);
         double modifiedDuration = CashFlows.duration(leg, yield, Duration.Type.Modified, includeSettlementDateFlows,
                                                      settlementDate, npvDate);

         double shift = 0.01;
         return (1.0 / (-npv * modifiedDuration)) * shift;
      }

      public static double yieldValueBasisPoint(Leg leg, double yield, DayCounter dayCounter, Compounding compounding,
                                                Frequency frequency, bool includeSettlementDateFlows, Date settlementDate = null,
                                                Date npvDate = null)
      {
         return yieldValueBasisPoint(leg, new InterestRate(yield, dayCounter, compounding, frequency),
                                     includeSettlementDateFlows, settlementDate, npvDate);
      }
      #endregion

      #region  Z-spread utility functions

      // NPV of the cash flows.
      //  The NPV is the sum of the cash flows, each discounted
      //  according to the z-spreaded term structure.  The result
      //  is affected by the choice of the z-spread compounding
      //  and the relative frequency and day counter.
      public static double npv(Leg leg, YieldTermStructure discountCurve, double zSpread, DayCounter dc, Compounding comp,
                               Frequency freq, bool includeSettlementDateFlows,
                               Date settlementDate = null, Date npvDate = null)
      {
         if (leg.empty())
            return 0.0;

         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         Handle<YieldTermStructure> discountCurveHandle = new Handle<YieldTermStructure>(discountCurve);
         Handle<Quote> zSpreadQuoteHandle = new Handle<Quote>(new SimpleQuote(zSpread));

         ZeroSpreadedTermStructure spreadedCurve = new ZeroSpreadedTermStructure(discountCurveHandle, zSpreadQuoteHandle,
                                                                                 comp, freq, dc);

         spreadedCurve.enableExtrapolation(discountCurveHandle.link.allowsExtrapolation());

         return npv(leg, spreadedCurve, includeSettlementDateFlows, settlementDate, npvDate);
      }
      //! implied Z-spread.
      public static double zSpread(Leg leg, double npv, YieldTermStructure discount, DayCounter dayCounter, Compounding compounding,
                                   Frequency frequency, bool includeSettlementDateFlows, Date settlementDate = null,
                                   Date npvDate = null, double accuracy = 1.0e-10, int maxIterations = 100, double guess = 0.0)
      {
         if (settlementDate == null)
            settlementDate = Settings.evaluationDate();

         if (npvDate == null)
            npvDate = settlementDate;

         Brent solver = new Brent();
         solver.setMaxEvaluations(maxIterations);
         ZSpreadFinder objFunction = new ZSpreadFinder(leg, discount, npv, dayCounter, compounding, frequency,
                                                       includeSettlementDateFlows, settlementDate, npvDate);
         double step = 0.01;
         return solver.solve(objFunction, accuracy, guess, step);
      }
      //! deprecated implied Z-spread.
      public static double zSpread(Leg leg, YieldTermStructure d, double npv, DayCounter dayCounter, Compounding compounding,
                                   Frequency frequency, bool includeSettlementDateFlows, Date settlementDate = null,
                                   Date npvDate = null, double accuracy = 1.0e-10, int maxIterations = 100,
                                   double guess = 0.0)
      {
         return zSpread(leg, npv, d, dayCounter, compounding, frequency,
                        includeSettlementDateFlows, settlementDate, npvDate,
                        accuracy, maxIterations, guess);
      }
      #endregion
   }


}
