**FREE
// CALL USRMNT: user maintenance (System Options on the main menu, option 1). Lists the users in the table
// QSIM_USER, where the servers (the 5250 sign-on, the sign-on server, Network Print and the remote
// command server) check sign-ons. A user's default library (DFTLIB) is the one its names are looked
// for in first, and where its new source and SQL tables go; QSIM_LIBRARY lists the libraries there are.
// F6 creates a user, 2 changes one, 4 deletes one and 5 lists a group profile's members. A change holds
// at the next sign-on.
// Group profiles are user profiles too: another profile names one as its group profile (GRPPRF) or a
// supplemental group (SUPGRPPRF). As on an IBM i, a group profile has no group profile of its own,
// supplemental groups need a group profile, and a group profile with members can't be deleted. Give
// one the password *NONE so that nobody signs on as it.
// Sim i keeps passwords as typed, so that every server can check them: don't use real ones.
dcl-f usrmntd workstn sfile(row:rrn);
dcl-s rrn int(5);
dcl-c AUTHORITIES ' *ALLOBJ *SECADM *JOBCTL *SPLCTL *SAVSYS *SERVICE *AUDIT *IOSYSCFG ';

dow not *in03;
  load();
  write keys;
  exfmt list;
  msg = '';
  if *in03 or *in12;
    leave;
  endif;
  if *in06;
    create();
    iter;
  endif;
  readc row;
  dow not %eof;
    if opt = '2';
      change();
    elseif opt = '4';
      remove();
    elseif opt = '5';
      members();
    endif;
    if *in03;
      leave;
    endif;
    readc row;
  enddo;
enddo;
*inlr = *on;
return;

// Clears the subfile and fills it with every user.
dcl-proc load;
  *in40 = *off;
  write list;
  rrn = 0;
  exec sql declare found cursor for
    select username, status, pwdexp, text, dftlib, grpprf from qsim_user order by username for read only;
  exec sql open found;
  exec sql fetch next from found into :uname, :ustatus, :upwdexp, :utext, :udftlib, :ugrpprf;
  dow sqlcode = 0;
    rrn += 1;
    opt = ' ';
    udesc = utext;
    write row;
    exec sql fetch next from found into :uname, :ustatus, :upwdexp, :utext, :udftlib, :ugrpprf;
  enddo;
  exec sql close found;
  *in40 = (rrn > 0);
end-proc;

// Asks for a new user until it's valid, or F12, and adds it.
dcl-proc create;
  uname = ' ';
  upwd = ' ';
  upwd2 = ' ';
  ustatus = '*ENABLED';
  upwdexp = '*NO';
  utext = ' ';
  udftlib = 'QGPL';
  ugrpprf = '*NONE';
  usupgrp = ' ';
  uspcaut = '*NONE';
  clearerrors();
  dou not invalid(*on);
    exfmt newuser;
    if *in12 or *in03;
      *in12 = *off;
      return;
    endif;
  enddo;
  exec sql insert into qsim_user (username, password, status, pwdexp, text, dftlib, grpprf, supgrpprf, spcaut)
    values (:uname, :upwd, :ustatus, :upwdexp, :utext, :udftlib, :ugrpprf, :usupgrp, :uspcaut);
  if sqlcode = 0;
    msg = 'User ' + %trim(uname) + ' created.';
  else;
    msg = 'Could not create the user: SQLCODE ' + %char(sqlcode);
  endif;
end-proc;

// Shows the user in UNAME for change, until it's valid, or F12. A blank password keeps the old one.
dcl-proc change;
  dcl-s wanted char(10);
  wanted = uname;
  exec sql select status, pwdexp, text, dftlib, grpprf, supgrpprf, spcaut
    into :ustatus, :upwdexp, :utext, :udftlib, :ugrpprf, :usupgrp, :uspcaut
    from qsim_user where username = :wanted;
  if sqlcode <> 0;
    msg = 'User ' + %trim(wanted) + ' was not found.';
    return;
  endif;
  uname = wanted;
  upwd = ' ';
  upwd2 = ' ';
  clearerrors();
  dou not invalid(*off);
    exfmt chguser;
    if *in12 or *in03;
      *in12 = *off;
      return;
    endif;
  enddo;
  if upwd = *blanks;
    exec sql update qsim_user set status = :ustatus, pwdexp = :upwdexp, text = :utext, dftlib = :udftlib,
      grpprf = :ugrpprf, supgrpprf = :usupgrp, spcaut = :uspcaut
      where username = :wanted;
  else;
    exec sql update qsim_user set password = :upwd, status = :ustatus, pwdexp = :upwdexp, text = :utext, dftlib = :udftlib,
      grpprf = :ugrpprf, supgrpprf = :usupgrp, spcaut = :uspcaut
      where username = :wanted;
  endif;
  if sqlcode = 0;
    msg = 'User ' + %trim(wanted) + ' changed.';
  else;
    msg = 'Could not change the user: SQLCODE ' + %char(sqlcode);
  endif;
end-proc;

// Asks to confirm deleting the user in UNAME. A group profile with members isn't deleted.
dcl-proc remove;
  dcl-s wanted char(10);
  dcl-s count int(10);
  wanted = uname;
  exec sql select text into :utext from qsim_user where username = :wanted;
  if sqlcode <> 0;
    msg = 'User ' + %trim(wanted) + ' was not found.';
    return;
  endif;
  count = membercount(wanted);
  if count > 0;
    msg = %trim(wanted) + ' is the group profile of ' + %char(count) + ' users: change theirs first.';
    return;
  endif;
  uname = wanted;
  exfmt confirm;
  if *in12 or *in03;
    *in12 = *off;
    return;
  endif;
  exec sql delete from qsim_user where username = :wanted;
  msg = 'User ' + %trim(wanted) + ' deleted.';
end-proc;

// Says which profiles have the user in UNAME as their group profile or a supplemental group.
dcl-proc members;
  dcl-s group varchar(10);
  dcl-s member char(10);
  dcl-s names varchar(200);
  group = %trim(uname);
  exec sql declare inside cursor for
    select username from qsim_user
    where grpprf = :group or position(' ' || :group || ' ' in ' ' || supgrpprf || ' ') > 0
    order by username for read only;
  exec sql open inside;
  exec sql fetch next from inside into :member;
  dow sqlcode = 0;
    names += ' ' + %trim(member);
    exec sql fetch next from inside into :member;
  enddo;
  exec sql close inside;
  if names = '';
    msg = group + ' has no members.';
  else;
    msg = 'Members of ' + group + ':' + names;
  endif;
end-proc;

// How many profiles have this one as their group profile or a supplemental group.
dcl-proc membercount;
  dcl-pi *n int(10);
    profile char(10) const;
  end-pi;
  dcl-s group varchar(10);
  dcl-s count int(10);
  group = %trim(profile);
  exec sql select count(*) into :count from qsim_user
    where grpprf = :group or position(' ' || :group || ' ' in ' ' || supgrpprf || ' ') > 0;
  return count;
end-proc;

// Whether this can be a group profile for UNAME: another profile, with no group profile of its own.
dcl-proc isgroup;
  dcl-pi *n ind;
    profile varchar(20) const;
  end-pi;
  dcl-s count int(10);
  if %len(profile) > 10 or profile = %trim(uname);
    return *off;
  endif;
  exec sql select count(*) into :count from qsim_user where username = :profile and grpprf = '*NONE';
  return count > 0;
end-proc;

dcl-proc clearerrors;
  *in50 = *off;
  *in51 = *off;
  *in52 = *off;
  *in53 = *off;
  *in54 = *off;
  *in55 = *off;
  *in56 = *off;
  *in57 = *off;
  *in58 = *off;
  *in59 = *off;
end-proc;

// Sets the error indicators (50 name, 51 exists, 52 password needed, 53 passwords differ, 54 status,
// 55 password expired, 56 library, 57 group profile, 58 supplemental groups, 59 special authorities)
// for what was typed, and tidies the lists. Returns whether any is on.
dcl-proc invalid;
  dcl-pi *n ind;
    creating ind const;
  end-pi;
  dcl-s count int(10);
  dcl-s word varchar(20);
  dcl-s list varchar(200);
  dcl-s groups int(10);
  dcl-c FIRST 'ABCDEFGHIJKLMNOPQRSTUVWXYZ#$@';
  dcl-c REST 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$@_';
  clearerrors();
  ustatus = %upper(ustatus);
  upwdexp = %upper(upwdexp);
  udftlib = %upper(udftlib);
  if udftlib = *blanks;
    udftlib = 'QGPL';
  endif;
  exec sql select count(*) into :count from qsim_library where name = :udftlib;
  *in56 = (count = 0);
  if creating;
    uname = %upper(uname);
    *in50 = (uname = *blanks
      or %check(FIRST:%subst(uname:1:1)) > 0
      or %check(REST:%trim(uname)) > 0);
    if not *in50;
      exec sql select count(*) into :count from qsim_user where username = :uname;
      *in51 = (count > 0);
    endif;
    *in52 = (upwd = *blanks);
  endif;
  // *NONE, in any case, is the profile with no password: nobody signs on as it.
  if %upper(%trim(upwd)) = '*NONE';
    upwd = '*NONE';
  else;
    *in53 = (upwd <> upwd2);
  endif;
  *in54 = (ustatus <> '*ENABLED' and ustatus <> '*DISABLED');
  *in55 = (upwdexp <> '*YES' and upwdexp <> '*NO');

  ugrpprf = %upper(ugrpprf);
  if ugrpprf = *blanks;
    ugrpprf = '*NONE';
  endif;
  if ugrpprf <> '*NONE';
    *in57 = not isgroup(%trim(ugrpprf));
    // A group profile with members can't be in a group itself.
    if not creating and membercount(uname) > 0;
      *in57 = *on;
    endif;
  endif;

  list = '';
  groups = 0;
  for-each word in %split(%upper(usupgrp));
    if word <> '*NONE' and %scan(' ' + word + ' ' : ' ' + list + ' ') = 0;
      groups += 1;
      if ugrpprf = '*NONE' or not isgroup(word) or word = %trim(ugrpprf) or groups > 15;
        *in58 = *on;
      endif;
      list = %trim(list + ' ' + word);
    endif;
  endfor;
  usupgrp = list;

  list = '';
  for-each word in %split(%upper(uspcaut));
    if word <> '*NONE' and %scan(' ' + word + ' ' : ' ' + list + ' ') = 0;
      if %scan(' ' + word + ' ' : AUTHORITIES) = 0;
        *in59 = *on;
      endif;
      list = %trim(list + ' ' + word);
    endif;
  endfor;
  if list = '';
    uspcaut = '*NONE';
  else;
    uspcaut = list;
  endif;
  return *in50 or *in51 or *in52 or *in53 or *in54 or *in55 or *in56 or *in57 or *in58 or *in59;
end-proc;
