import {
  AMENITY,
  BUILDING_B,
  bookViaForm,
  expect,
  gotoApp,
  openAmenity,
  selectBuilding,
  selectResident,
  test,
  toDatetimeLocal,
  ui,
} from './helpers';

test.describe('reservations view and form', () => {
  test('S11 all upcoming bookings show together, across amenities', async ({ page, scenario }) => {
    await gotoApp(page);

    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    await selectResident(page, 'bob');
    await openAmenity(page, AMENITY.gymA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'bob' });

    // Only the Gym is selected, yet both bookings are listed, each labelled with its amenity.
    await expect(ui.allRows(page)).toHaveCount(2);
    await expect(ui.rowsOf(page, 'alice')).toContainText('Party Room');
    await expect(ui.rowsOf(page, 'bob')).toContainText('Gym');
  });

  test('S12 a page refresh keeps the building, resident, open amenity and bookings', async ({ page, scenario }) => {
    await gotoApp(page);
    await selectBuilding(page, BUILDING_B);
    await selectResident(page, 'carol');
    await openAmenity(page, AMENITY.partyRoomB);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'carol' });

    await page.reload();

    await expect(ui.buildingSwitcher(page)).toHaveValue(BUILDING_B);
    await expect(ui.residentSwitcher(page)).toHaveValue('carol');
    await expect(page.getByTestId('book-submit')).toBeVisible(); // the amenity is still open
    await expect(ui.rowsOf(page, 'carol')).toHaveCount(1);
  });

  test('S13 end time follows the start, shows the duration, and nothing is shown in UTC', async ({ page, scenario }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);

    await page.getByTestId('book-start').fill(toDatetimeLocal(scenario.at(10)));
    await expect(page.getByTestId('book-end')).toHaveValue(toDatetimeLocal(scenario.at(11)));
    await expect(page.getByText('Duration: 60 min')).toBeVisible();

    await page.getByTestId('book-end').fill(toDatetimeLocal(scenario.at(12, 30)));
    await expect(page.getByText('Duration: 2 h 30 min')).toBeVisible();
    await page.getByTestId('book-submit').click();

    const row = ui.rowsOf(page, 'alice');
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Alice'); // names are capitalized
    await expect(row).toContainText('(2 h 30 min)'); // over 90 minutes reads as hours and minutes
    await expect(page.locator('body')).not.toContainText('UTC');
  });
});
